using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;
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

        return group;
    }
}
