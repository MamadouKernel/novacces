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
            var callerIsSuperAdmin = NovAccesAuthorizationMatrix.CanViewAllUsers(caller);

            var result = new List<AdminUserDto>(all.Count);
            foreach (var u in all)
            {
                var roles = await users.GetRolesAsync(u);

                // Les comptes SuperAdmin sont invisibles pour les autres rôles.
                // Seul un SuperAdmin peut consulter la flotte complète.
                if (!callerIsSuperAdmin && roles.Contains(NovAccesRoles.SuperAdmin))
                    continue;

                result.Add(new AdminUserDto(u.Id, u.Email!, u.DisplayName, roles.ToList(), u.SiteId, u.TwoFactorEnabled, u.IsDeactivated));
            }

            return Results.Ok(result);
        })
        .WithName("AdminListUsers")
        .WithSummary("Liste les comptes (tous sites).");
        // Désactivation logique d'un compte : aucun DELETE physique. Le contrôle
        // hiérarchique est centralisé dans NovAccesAuthorizationMatrix.
        group.MapPost("/users/{id:guid}/deactivate", async (
            Guid id,
            DeactivateUserRequestDto request,
            ClaimsPrincipal caller,
            UserManager<ApplicationUser> users,
            NovAccesIdentityDbContext identityDb,
            IRefreshTokenService refresh,
            IDateTimeProvider clock,
            CurrentTenant tenant,
            IAdminAuditLog audit,
            CancellationToken ct) =>
        {
            var reason = request?.Reason?.Trim();
            if (string.IsNullOrWhiteSpace(reason) || reason.Length < 5 || reason.Length > 500)
                return Results.BadRequest(new { error = "Un motif de désactivation (5 à 500 caractères) est obligatoire." });

            var actor = await users.GetUserAsync(caller);
            if (actor is null)
                return Results.Unauthorized();

            await using var transaction = await identityDb.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, ct);

            var target = await users.FindByIdAsync(id.ToString());
            if (target is null)
                return Results.NotFound(new { error = "Compte introuvable." });

            if (target.IsDeactivated)
                return Results.Conflict(new { error = "Ce compte est déjà désactivé." });

            var targetRoles = await users.GetRolesAsync(target);
            if (!NovAccesAuthorizationMatrix.CanDeactivateAccount(caller, targetRoles))
                return Results.Json(
                    new { error = "Votre rôle ne permet pas de désactiver ce compte." },
                    statusCode: StatusCodes.Status403Forbidden);

            if (targetRoles.Contains(NovAccesRoles.SuperAdmin, StringComparer.Ordinal))
            {
                var activeSuperAdmins = (await users.GetUsersInRoleAsync(NovAccesRoles.SuperAdmin))
                    .Count(u => !u.IsDeactivated);
                if (activeSuperAdmins <= 1)
                    return Results.Conflict(new
                    {
                        error = "Le dernier SuperAdmin actif ne peut pas être désactivé."
                    });
            }

            target.Deactivate(clock.UtcNow);
            var updated = await users.UpdateAsync(target);
            if (!updated.Succeeded)
                return Results.BadRequest(new
                {
                    error = "Désactivation du compte refusée.",
                    details = updated.Errors.Select(e => e.Description)
                });

            await refresh.RevokeAllForSubjectAsync("user", target.Id.ToString(), ct);
            await transaction.CommitAsync(ct);

            // Le journal métier est cloisonné par site. Les comptes globaux
            // restent couverts par le journal technique transversal de la requête.
            if (!string.IsNullOrWhiteSpace(target.SiteId)
                && CurrentTenant.IsValidSiteId(target.SiteId))
            {
                tenant.Resolve(target.SiteId);
                await audit.RecordAsync(
                    AdminAuditAction.AccountDeactivated,
                    actor.Id.ToString(),
                    target.Id.ToString(),
                    $"Compte désactivé. Motif : {reason}",
                    ct);
            }

            return Results.Ok(new
            {
                message = "Compte désactivé. Les données historiques sont conservées.",
                userId = target.Id,
                target.IsDeactivated
            });
        })
        .WithName("AdminDeactivateUser")
        .WithSummary("Désactive logiquement un compte et révoque ses sessions.");

        // Provisionnement d'un site depuis la console : désormais possible en HTTP
        // car protégé par le rôle Admin (auth en place). Le service reste aussi
        // disponible en CLI pour l'exploitation (dotnet run -- provision-site).
        group.MapPost("/sites", async (
            ProvisionSiteRequestDto request,
            ITenantProvisioningService provisioning,
            CancellationToken ct) =>
        {
            var siteId = request.SiteId?.Trim().ToLowerInvariant();
            if (!CurrentTenant.IsValidSiteId(siteId))
                return Results.BadRequest(new { error = "Identifiant de site invalide (a-z, 0-9, _ ; max 40)." });

            await provisioning.ProvisionAsync(siteId!, ct);
            return Results.Ok(new { message = $"Site '{siteId}' provisionné." });
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
            var siteId = request.SiteId?.Trim().ToLowerInvariant();
            var matricule = request.Matricule?.Trim();
            var displayName = request.DisplayName?.Trim();
            var pin = request.Pin?.Trim();
            if (!CurrentTenant.IsValidSiteId(siteId))
                return Results.BadRequest(new { error = "Identifiant de site invalide." });
            if (string.IsNullOrWhiteSpace(matricule) || matricule.Length > 80
                || string.IsNullOrWhiteSpace(displayName) || displayName.Length > 160)
                return Results.BadRequest(new { error = "Matricule et nom requis." });
            if (string.IsNullOrWhiteSpace(pin) || pin.Length is < 4 or > 8 || !pin.All(char.IsDigit))
                return Results.BadRequest(new { error = "PIN numérique de 4 à 8 chiffres requis." });

            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentTenant>().Resolve(siteId!);
            var agents = scope.ServiceProvider.GetRequiredService<IAgentDirectory>();
            var audit = scope.ServiceProvider.GetRequiredService<IAdminAuditLog>();

            try
            {
                await agents.AddAsync(matricule!, displayName!, pin!, ct);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }

            await audit.RecordAsync(AdminAuditAction.AgentCreated, user.HostIdentifier(), matricule!,
                $"Création de l'agent « {displayName} » (matricule {matricule}).", ct);

            return Results.Ok(new { message = $"Agent {matricule} créé pour le site {siteId}." });
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
            ISiteCatalog sites,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var label = request.Label?.Trim();
            if (string.IsNullOrWhiteSpace(label) || label.Length > 120)
                return Results.BadRequest(new { error = "Libellé requis (120 caractères maximum)." });

            var siteIds = (request.SiteIds ?? Array.Empty<string>())
                .Select(s => s.Trim().ToLowerInvariant())
                .Distinct()
                .ToList();

            if (siteIds.Count == 0)
                return Results.BadRequest(new { error = "Au moins un site requis." });

            if (siteIds.Any(s => !CurrentTenant.IsValidSiteId(s)))
                return Results.BadRequest(new { error = "Un des identifiants de site est invalide." });

            var provisioned = await sites.GetSiteIdsAsync(ct);
            if (siteIds.Any(site => !provisioned.Contains(site, StringComparer.OrdinalIgnoreCase)))
                return Results.BadRequest(new { error = "Tous les sites doivent être provisionnés avant l'enrôlement du terminal." });

            var id = await terminals.CreateAsync(label!, siteIds, ct);

            // Le terminal n'appartient à aucun tenant en particulier (il peut en
            // servir plusieurs) : l'action est inscrite dans le journal d'audit
            // de CHAQUE site concerné, pour que la sûreté/l'admin de ce site
            // voie qu'un terminal a été autorisé à le servir.
            await RecordOnEachSiteAsync(scopeFactory, siteIds,
                AdminAuditAction.TerminalCreated, user.HostIdentifier(), id.ToString(),
                $"Terminal « {label} » enrôlé pour {string.Join(", ", siteIds)}.", ct);

            return Results.Created($"/api/admin/terminals/{id:D}", new CreateTerminalResponseDto(id, label!));
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
            IHostEnvironment environment,
            CancellationToken ct) =>
        {
            var minutes = configuration.GetValue<double?>("Enrollment:TicketLifetimeMinutes") ?? 60d;
            minutes = Math.Clamp(minutes, 1d, 60d);
            var ticket = await terminals.CreateEnrollmentTicketAsync(
                id, user.HostIdentifier(), TimeSpan.FromMinutes(minutes), ct);
            if (ticket is null)
                return Results.NotFound(new { error = "Terminal introuvable ou révoqué." });

            var configuredBaseUrl = configuration["Api:PublicBaseUrl"]?.Trim();
            string publicBaseUrl;
            if (string.IsNullOrWhiteSpace(configuredBaseUrl))
            {
                if (environment.IsProduction())
                    return Results.Problem("Api:PublicBaseUrl doit être configurée en production.", statusCode: StatusCodes.Status500InternalServerError);
                publicBaseUrl = $"{http.Scheme}://{http.Host}";
            }
            else if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var baseUri)
                || baseUri.Scheme is not ("https" or "http")
                || !string.IsNullOrEmpty(baseUri.Query)
                || !string.IsNullOrEmpty(baseUri.Fragment))
            {
                return Results.Problem("Api:PublicBaseUrl doit être une URL absolue sans query ni fragment.", statusCode: StatusCodes.Status500InternalServerError);
            }
            else if (environment.IsProduction() && !string.Equals(baseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem("Api:PublicBaseUrl doit utiliser HTTPS en production.", statusCode: StatusCodes.Status500InternalServerError);
            }
            else
            {
                publicBaseUrl = configuredBaseUrl.TrimEnd('/');
            }
            var qrPayload = $"novacces://enroll?api={Uri.EscapeDataString(publicBaseUrl)}&ticket={Uri.EscapeDataString(ticket.Ticket)}&terminal={ticket.TerminalId:D}&expires={ticket.ExpiresAt.ToUnixTimeSeconds()}";

            await RecordOnEachSiteAsync(scopeFactory, ticket.SiteIds,
                AdminAuditAction.EnrollmentTicketCreated, user.HostIdentifier(), ticket.TerminalId.ToString(),
                $"Ticket QR temporaire créé pour le terminal « {ticket.Label} » (expiration {ticket.ExpiresAt:O}).", ct);

            return Results.Ok(new EnrollmentTicketResponseDto(
                ticket.TerminalId, ticket.Label, ticket.SiteIds, qrPayload, ticket.ExpiresAt));
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
