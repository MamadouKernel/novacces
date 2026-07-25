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

        group.MapGet("/journal", async (IScanLogRepository logs, int? limit, string? q, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var entries = await logs.GetRecentAsync(take, q, ct);

            var dto = entries.Select(e => new ScanJournalEntryDto(
                e.Timestamp, e.VisitorName, e.AgentId, e.Direction.ToString(),
                e.WasGranted, e.WasCheckOut, e.IsSecurityEvent, e.Detail)).ToList();

            return Results.Ok(dto);
        })
        .WithName("ScanJournal")
        .WithSummary("Derniers scans journalisés du site (recherche via 'q').");

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

            var scans = today.Count;
            var denied = today.Count(e => !e.WasGranted);
            var securityEvents = today.Count(e => e.IsSecurityEvent);
            var overstays = onSite.Count(v => v.OverstayLevel > 0);

            // Pic d'affluence : heure locale la plus chargée du jour.
            int? peakHour = today.Count > 0
                ? today.GroupBy(e => e.Timestamp.ToLocalTime().Hour)
                       .OrderByDescending(g => g.Count()).First().Key
                : null;

            // Appréciation du taux de refus (heuristique simple, ajustable).
            var refusalRate = scans > 0 ? (double)denied / scans : 0;
            var appreciation = scans < 10 ? "activité faible"
                : refusalRate <= 0.15 ? "dans la normale"
                : "inhabituel, à surveiller";

            // Recommandation textuelle adaptée à la situation.
            var recommendation =
                securityEvents > 0 ? "Événements de sécurité détectés : consulter le journal et vérifier les présences signalées."
                : overstays > 0 ? "Des visiteurs dépassent leur durée prévue : vérifier leur présence."
                : refusalRate > 0.15 ? "Taux de refus inhabituel : renforcer la vigilance aux postes."
                : "Aucune action requise.";

            var summary = new DashboardSummaryDto(
                ScansToday: scans,
                EntriesGranted: today.Count(e => e is { WasGranted: true, WasCheckOut: false }),
                Exits: today.Count(e => e.WasCheckOut),
                Denied: denied,
                SecurityEvents: securityEvents,
                OnSite: onSite.Count,
                PeakHour: peakHour,
                RefusalAppreciation: appreciation,
                Recommendation: recommendation);

            return Results.Ok(summary);
        })
        .WithName("DashboardSummary")
        .WithSummary("Synthèse du jour (scans, entrées, sorties, refus, présents).");

        group.MapGet("/journal.csv", async (IScanLogRepository logs, int? limit, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 200, 1, 2000);
            var entries = await logs.GetRecentAsync(take, null, ct);
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

    // Échappement CSV (séparateur ';') + neutralisation de l'injection de formule.
    private static string Csv(string value)
    {
        value ??= "";

        // Anti-injection de formule (Excel / LibreOffice / Google Sheets) : une
        // cellule commençant par = + - @ (ou tabulation / retour chariot) serait
        // évaluée comme une formule à l'ouverture — y compris malgré le
        // guillemetage CSV. On préfixe donc d'une apostrophe (reco. OWASP), ce
        // qui force le texte brut sans altérer la lisibilité du nom.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;

        if (value.Contains(';') || value.Contains('"') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
