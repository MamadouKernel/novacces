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

        group.MapGet("/users", async (UserManager<ApplicationUser> users, CancellationToken ct) =>
        {
            var all = await users.Users.OrderBy(u => u.Email).ToListAsync(ct);

            var result = new List<AdminUserDto>(all.Count);
            foreach (var u in all)
            {
                var roles = await users.GetRolesAsync(u);
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

        return group;
    }
}
