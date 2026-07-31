using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Enums;
using NovAcces.Infrastructure.Identity;
using NovAcces.Infrastructure.Persistence.Tenancy;
using NovAcces.Infrastructure.Retention;
using NovAcces.Shared.Auth;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Endpoints;

/// <summary>
/// Console d'administration (Admin Sigasécurité, global). Gestion des comptes et
/// provisionnement des sites. La création de compte réutilise /api/auth/register.
/// </summary>
public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin").WithTags("Admin")
            .RequireAuthorization(NovAccesRoles.Admin);

        group.MapGet("/overview", async (ISiteOverviewService overview, CancellationToken ct) =>
        {
            var sites = await overview.GetAsync(ct);
            var dto = sites.Select(s => new AdminSiteOverviewDto(s.SiteId, s.OnSite, s.ScansToday)).ToList();
            return Results.Ok(dto);
        })
        .WithName("AdminOverview")
        .WithSummary("Vue consolidée multi-sites : présents et scans du jour par site.");

        group.MapGet("/users", async (
            ClaimsPrincipal caller,
            UserManager<ApplicationUser> users,
            CancellationToken ct) =>
        {
            var all = await users.Users.OrderBy(u => u.Email).ToListAsync(ct);
            var callerIsSuperAdmin = caller.IsInRole(NovAccesRoles.SuperAdmin);

            var result = new List<AdminUserDto>(all.Count);
            foreach (var u in all)
            {
                var roles = await users.GetRolesAsync(u);

                // Les comptes SuperAdmin sont invisibles pour les autres rôles.
                // Seul un SuperAdmin peut consulter la flotte complète.
                if (!callerIsSuperAdmin && roles.Contains(NovAccesRoles.SuperAdmin))
                    continue;

                result.Add(new AdminUserDto(u.Id, u.Email!, u.DisplayName, roles.ToList(), u.SiteId, u.TwoFactorEnabled));
            }

            return Results.Ok(result);
        })
        .WithName("AdminListUsers")
        .WithSummary("Liste les comptes (tous sites).");

        // Provisionnement d'un site depuis la console : désormais possible en HTTP
        // car protégé par le rôle Admin (auth en place). Le service reste aussi
        // disponible en CLI pour l'exploitation (dotnet run -- provision-site).
        group.MapPost("/sites", async (
            ProvisionSiteRequestDto request,
            ITenantProvisioningService provisioning,
            CancellationToken ct) =>
        {
            if (!CurrentTenant.IsValidSiteId(request.SiteId))
                return Results.BadRequest(new { error = "Identifiant de site invalide (a-z, 0-9, _ ; max 40)." });

            await provisioning.ProvisionAsync(request.SiteId, ct);
            return Results.Ok(new { message = $"Site '{request.SiteId}' provisionné." });
        })
        .WithName("AdminProvisionSite")
        .WithSummary("Provisionne un nouveau site (schéma + modèle + journal append-only).");

        // --- Rétention / purge des données personnelles (§7.3) ---

        group.MapGet("/retention", (IOptions<RetentionOptions> options) =>
        {
            var o = options.Value;
            return Results.Ok(new RetentionStatusDto(
                o.Enabled, o.VisitRetentionDays, o.JournalRetentionDays, o.RunIntervalHours));
        })
        .WithName("AdminRetentionStatus")
        .WithSummary("Politique de rétention en vigueur (conservation, anonymisation, intervalle).");

        // Déclenchement manuel d'une passe de rétention (en plus de la passe
        // automatique du RetentionMonitor). Action privilégiée : chaque site
        // traité est inscrit à son journal d'audit inaltérable.
        group.MapPost("/retention/run", async (IDataRetentionService retention, CancellationToken ct) =>
        {
            var results = await retention.PurgeOnceAsync(ct);
            var dto = new RetentionRunResultDto(
                results.Sum(r => r.VisitsPurged),
                results.Sum(r => r.ScanLogsAnonymized),
                results.Select(r => new SitePurgeDto(r.SiteId, r.VisitsPurged, r.ScanLogsAnonymized)).ToList());
            return Results.Ok(dto);
        })
        .WithName("AdminRetentionRun")
        .WithSummary("Déclenche une passe immédiate de rétention : purge + anonymisation des journaux (§7.3).");

        // --- Agents (prise de poste matricule + PIN) ---
        // Création réservée à l'Admin. Le PIN est haché côté annuaire. On résout
        // le tenant du site cible dans un scope dédié (comme le provisionnement).
        group.MapPost("/agents", async (
            CreateAgentRequestDto request,
            IServiceScopeFactory scopeFactory,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (!CurrentTenant.IsValidSiteId(request.SiteId))
                return Results.BadRequest(new { error = "Identifiant de site invalide." });
            if (string.IsNullOrWhiteSpace(request.Matricule) || string.IsNullOrWhiteSpace(request.DisplayName))
                return Results.BadRequest(new { error = "Matricule et nom requis." });
            if ((request.Pin ?? string.Empty).Trim().Length < 4)
                return Results.BadRequest(new { error = "PIN d'au moins 4 chiffres requis." });

            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentTenant>().Resolve(request.SiteId);
            var agents = scope.ServiceProvider.GetRequiredService<IAgentDirectory>();
            var audit = scope.ServiceProvider.GetRequiredService<IAdminAuditLog>();

            try
            {
                await agents.AddAsync(request.Matricule, request.DisplayName, request.Pin, ct);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }

            await audit.RecordAsync(AdminAuditAction.AgentCreated, user.HostIdentifier(), request.Matricule,
                $"Création de l'agent « {request.DisplayName} » (matricule {request.Matricule}).", ct);

            return Results.Ok(new { message = $"Agent {request.Matricule} créé pour le site {request.SiteId}." });
        })
        .WithName("AdminCreateAgent")
        .WithSummary("Crée un agent (matricule + PIN) pour la prise de poste sur un site.");

        group.MapGet("/agents/{siteId}", async (
            string siteId,
            IServiceScopeFactory scopeFactory,
            CancellationToken ct) =>
        {
            if (!CurrentTenant.IsValidSiteId(siteId))
                return Results.BadRequest(new { error = "Identifiant de site invalide." });

            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentTenant>().Resolve(siteId);
            var agents = scope.ServiceProvider.GetRequiredService<IAgentDirectory>();

            var list = await agents.ListAsync(ct);
            return Results.Ok(list.Select(a => new AgentSummaryDto(a.Matricule, a.DisplayName)).ToList());
        })
        .WithName("AdminListAgents")
        .WithSummary("Liste les agents (matricule + nom) d'un site.");

        // --- Terminaux (enrôlement) ---
        // Contrairement aux agents, un terminal ne vit dans le schéma d'AUCUN
        // site (il peut en servir plusieurs) : ITerminalDirectory est adossé au
        // schéma partagé « identity », pas besoin de résoudre un tenant ici.
        group.MapPost("/terminals", async (
            CreateTerminalRequestDto request,
            ITerminalDirectory terminals,
            IServiceScopeFactory scopeFactory,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Label))
                return Results.BadRequest(new { error = "Libellé requis." });

            var siteIds = (request.SiteIds ?? Array.Empty<string>())
                .Select(s => s.Trim().ToLowerInvariant())
                .Distinct()
                .ToList();

            if (siteIds.Count == 0)
                return Results.BadRequest(new { error = "Au moins un site requis." });

            if (siteIds.Any(s => !CurrentTenant.IsValidSiteId(s)))
                return Results.BadRequest(new { error = "Un des identifiants de site est invalide." });

            var (id, apiKey) = await terminals.CreateAsync(request.Label, siteIds, ct);

            // Le terminal n'appartient à aucun tenant en particulier (il peut en
            // servir plusieurs) : l'action est inscrite dans le journal d'audit
            // de CHAQUE site concerné, pour que la sûreté/l'admin de ce site
            // voie qu'un terminal a été autorisé à le servir.
            await RecordOnEachSiteAsync(scopeFactory, siteIds,
                AdminAuditAction.TerminalCreated, user.HostIdentifier(), id.ToString(),
                $"Terminal « {request.Label} » enrôlé pour {string.Join(", ", siteIds)}.", ct);

            return Results.Ok(new CreateTerminalResponseDto(id, request.Label, apiKey));
        })
        .WithName("AdminCreateTerminal")
        .WithSummary("Provisionne un terminal ; son activation se fait par ticket QR temporaire.");

        // Ticket QR temporaire : aucune clé API n'est affichée dans le Web.
        group.MapPost("/terminals/{id:guid}/enrollment-ticket", async (
            Guid id,
            ITerminalDirectory terminals,
            IServiceScopeFactory scopeFactory,
            ClaimsPrincipal user,
            HttpRequest http,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            var minutes = configuration.GetValue<double?>("Enrollment:TicketLifetimeMinutes") ?? 60d;
            minutes = Math.Clamp(minutes, 1d, 60d);
            var ticket = await terminals.CreateEnrollmentTicketAsync(
                id, user.HostIdentifier(), TimeSpan.FromMinutes(minutes), ct);
            if (ticket is null)
                return Results.NotFound(new { error = "Terminal introuvable ou révoqué." });

            var configuredBaseUrl = configuration["Api:PublicBaseUrl"];
            var publicBaseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
                ? http.Scheme + "://" + http.Host
                : configuredBaseUrl.TrimEnd('/');
            var qrPayload = $"novacces://enroll?api={Uri.EscapeDataString(publicBaseUrl)}&ticket={Uri.EscapeDataString(ticket.Ticket)}&terminal={ticket.TerminalId:D}&expires={ticket.ExpiresAt.ToUnixTimeSeconds()}";

            await RecordOnEachSiteAsync(scopeFactory, ticket.SiteIds,
                AdminAuditAction.EnrollmentTicketCreated, user.HostIdentifier(), ticket.TerminalId.ToString(),
                $"Ticket QR temporaire créé pour le terminal « {ticket.Label} » (expiration {ticket.ExpiresAt:O}).", ct);

            return Results.Ok(new EnrollmentTicketResponseDto(
                ticket.TerminalId, ticket.Label, ticket.SiteIds, ticket.Ticket, qrPayload, ticket.ExpiresAt));
        })
        .RequireRateLimiting("sensitive")
        .WithName("CreateTerminalEnrollmentTicket")
        .WithSummary("Génère un QR d'enrôlement temporaire, à usage unique.");

        group.MapGet("/terminals", async (ITerminalDirectory terminals, CancellationToken ct) =>
        {
            var list = await terminals.ListAsync(ct);
            return Results.Ok(list.Select(t =>
                new TerminalSummaryDto(t.Id, t.Label, t.SiteIds, t.IsActive, t.CreatedAt, t.IsEnrolled)).ToList());
        })
        .WithName("AdminListTerminals")
        .WithSummary("Liste les terminaux enrôlés (jamais leur clé).");

        group.MapPost("/terminals/{id:guid}/revoke", async (
            Guid id,
            ITerminalDirectory terminals,
            IServiceScopeFactory scopeFactory,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var before = await terminals.ListAsync(ct);
            var target = before.FirstOrDefault(t => t.Id == id);
            if (target is null)
                return Results.NotFound(new { error = "Terminal introuvable." });

            await terminals.RevokeAsync(id, ct);

            await RecordOnEachSiteAsync(scopeFactory, target.SiteIds,
                AdminAuditAction.TerminalRevoked, user.HostIdentifier(), id.ToString(),
                $"Terminal « {target.Label} » révoqué.", ct);

            return Results.Ok(new { message = "Terminal révoqué." });
        })
        .WithName("AdminRevokeTerminal")
        .WithSummary("Révoque un terminal (clé désactivée, historique conservé).");

        return group;
    }

    // Un terminal n'appartient à aucun tenant précis (il peut en servir
    // plusieurs) : on inscrit l'action dans le journal d'audit de CHAQUE site
    // concerné, un scope + une résolution de tenant par site (même schéma que
    // la création d'agent, ci-dessus).
    private static async Task RecordOnEachSiteAsync(
        IServiceScopeFactory scopeFactory, IReadOnlyList<string> siteIds,
        AdminAuditAction action, string actor, string? targetId, string detail, CancellationToken ct)
    {
        foreach (var siteId in siteIds)
        {
            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentTenant>().Resolve(siteId);
            var audit = scope.ServiceProvider.GetRequiredService<IAdminAuditLog>();
            await audit.RecordAsync(action, actor, targetId, detail, ct);
        }
    }
}
