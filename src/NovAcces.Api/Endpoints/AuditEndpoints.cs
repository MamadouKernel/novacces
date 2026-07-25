using NovAcces.Application.Abstractions;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Endpoints;

/// <summary>
/// Consultation du journal d'audit des actions d'administration/sûreté du site
/// (section 8.5 du CDC). Réservé à la Sûreté et l'Admin (même privilège que la
/// gestion de la liste d'exclusion). Le journal est per-site (tenant résolu par
/// la requête) et inaltérable côté base : cet endpoint ne fait que lire.
/// </summary>
public static class AuditEndpoints
{
    public static RouteGroupBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/audit").WithTags("Audit")
            .RequireAuthorization("ManageExclusions");

        group.MapGet("/", async (IAdminAuditLog audit, int? limit, CancellationToken ct) =>
        {
            var entries = await audit.GetRecentAsync(Math.Clamp(limit ?? 100, 1, 500), ct);
            var dto = entries
                .Select(e => new AdminAuditDto(
                    e.Id, e.Actor, e.Action.ToString(), e.TargetId, e.Detail, e.Timestamp))
                .ToList();
            return Results.Ok(dto);
        })
        .WithName("ListAuditEntries")
        .WithSummary("Journal d'audit des actions privilégiées du site (§8.5).");

        return group;
    }
}
