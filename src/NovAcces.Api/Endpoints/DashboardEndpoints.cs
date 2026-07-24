using NovAcces.Application.Abstractions;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Endpoints;

/// <summary>
/// Lectures du dashboard sûreté (REQ-F-07). Réservées à la policy « Dashboard »
/// (Sûreté / Hôte / Admin). Le tenant vient du jeton : chaque appelant ne voit
/// que le journal et les présents de SON site.
/// </summary>
public static class DashboardEndpoints
{
    public static RouteGroupBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dashboard").WithTags("Dashboard")
            .RequireAuthorization("Dashboard");

        group.MapGet("/journal", async (IScanLogRepository logs, int? limit, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var entries = await logs.GetRecentAsync(take, ct);

            var dto = entries.Select(e => new ScanJournalEntryDto(
                e.Timestamp, e.VisitorName, e.AgentId, e.Direction.ToString(),
                e.WasGranted, e.WasCheckOut, e.IsSecurityEvent, e.Detail)).ToList();

            return Results.Ok(dto);
        })
        .WithName("ScanJournal")
        .WithSummary("Derniers scans journalisés du site.");

        group.MapGet("/on-site", async (IVisitRepository visits, CancellationToken ct) =>
        {
            var onSite = await visits.GetOnSiteAsync(ct);

            var dto = onSite.Select(v => new OnSiteVisitorDto(
                v.Id, v.VisitorName, v.VisitorCompany, v.CheckedInAt)).ToList();

            return Results.Ok(dto);
        })
        .WithName("OnSiteVisitors")
        .WithSummary("Visiteurs actuellement présents sur le site.");

        return group;
    }
}
