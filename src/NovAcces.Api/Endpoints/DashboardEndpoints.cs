using System.Globalization;
using System.Text;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Entities;
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

        group.MapGet("/on-site", async (IVisitRepository visits, IDateTimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.UtcNow;
            var onSite = await visits.GetOnSiteAsync(ct);

            var dto = onSite.Select(v => new OnSiteVisitorDto(
                v.Id, v.VisitorName, v.VisitorCompany, v.CheckedInAt,
                v.ComputeOverstayMinutes(now), v.OverstayLevel)).ToList();

            return Results.Ok(dto);
        })
        .WithName("OnSiteVisitors")
        .WithSummary("Visiteurs actuellement présents sur le site.");

        group.MapGet("/summary", async (
            IScanLogRepository logs, IVisitRepository visits, IDateTimeProvider clock, CancellationToken ct) =>
        {
            var dayStart = new DateTimeOffset(clock.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
            var today = await logs.GetSinceAsync(dayStart, ct);
            var onSite = await visits.GetOnSiteAsync(ct);

            var summary = new DashboardSummaryDto(
                ScansToday: today.Count,
                EntriesGranted: today.Count(e => e is { WasGranted: true, WasCheckOut: false }),
                Exits: today.Count(e => e.WasCheckOut),
                Denied: today.Count(e => !e.WasGranted),
                SecurityEvents: today.Count(e => e.IsSecurityEvent),
                OnSite: onSite.Count);

            return Results.Ok(summary);
        })
        .WithName("DashboardSummary")
        .WithSummary("Synthèse du jour (scans, entrées, sorties, refus, présents).");

        group.MapGet("/journal.csv", async (IScanLogRepository logs, int? limit, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 200, 1, 2000);
            var entries = await logs.GetRecentAsync(take, ct);
            var csv = BuildCsv(entries);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", "journal-scans.csv");
        })
        .WithName("DashboardJournalCsv")
        .WithSummary("Export CSV du journal des scans.");

        return group;
    }

    private static string BuildCsv(IReadOnlyCollection<ScanLogEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Horodatage;Visiteur;Agent;Direction;Autorise;Sortie;EvenementSecurite;Detail");
        foreach (var e in entries)
        {
            sb.Append(e.Timestamp.ToString("o", CultureInfo.InvariantCulture)).Append(';')
              .Append(Csv(e.VisitorName)).Append(';')
              .Append(Csv(e.AgentId)).Append(';')
              .Append(e.Direction).Append(';')
              .Append(e.WasGranted ? "oui" : "non").Append(';')
              .Append(e.WasCheckOut ? "oui" : "non").Append(';')
              .Append(e.IsSecurityEvent ? "oui" : "non").Append(';')
              .Append(Csv(e.Detail)).AppendLine();
        }
        return sb.ToString();
    }

    // Échappement CSV (séparateur ';') : encadre de guillemets si nécessaire.
    private static string Csv(string value)
    {
        if (value.Contains(';') || value.Contains('"') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
