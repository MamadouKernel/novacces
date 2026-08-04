using NovAcces.Shared.Auth;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Endpoints;

/// <summary>
/// Lectures du dashboard sûreté (REQ-F-07). Réservées à la Sûreté et l'Admin
/// (policy « SecurityJournal ») : ces vues portent sur TOUS les visiteurs du
/// site, pas seulement ceux d'un hôte donné. Un hôte consulte ses propres
/// demandes via /api/visits/mine et /api/visits/{id}/history, qui filtrent sur
/// lui — moindre privilège §8.5.
///
/// Le tenant vient du jeton : chaque appelant ne voit que le journal et les
/// présents de SON site.
/// </summary>
public static class DashboardEndpoints
{
    public static RouteGroupBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dashboard").WithTags("Dashboard")
            .RequireAuthorization(NovAccesPolicies.SecurityJournal);

        group.MapGet("/journal", async (
            IScanLogRepository logs, int? page, int? pageSize, string? q, CancellationToken ct) =>
        {
            var (p, size) = PaginationQuery.Normalize(page, pageSize, defaultPageSize: 20, maxPageSize: 200);
            var (entries, total) = await logs.GetPagedAsync(p, size, q, ct);

            var dto = entries.Select(e => new ScanJournalEntryDto(
                e.Timestamp, e.VisitorName, e.AgentId, e.Direction.ToString(),
                e.WasGranted, e.WasCheckOut, e.IsSecurityEvent, e.Detail, e.AuthMethod.ToString())).ToList();

            return Results.Ok(new PagedResultDto<ScanJournalEntryDto>(dto, p, size, total));
        })
        .WithName("ScanJournal")
        .WithSummary("Scans journalisés du site, paginés (recherche via 'q').");

        group.MapGet("/on-site", async (IVisitRepository visits, IDateTimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.UtcNow;
            var onSite = await visits.GetOnSiteAsync(ct);

            var dto = onSite.Select(v => new OnSiteVisitorDto(
                v.Id, v.VisitorName, v.VisitorCompany, v.CheckedInAt,
                v.ComputeOverstayMinutes(now), v.OverstayLevel, v.PlannedDurationMinutes)).ToList();

            return Results.Ok(dto);
        })
        .WithName("OnSiteVisitors")
        .WithSummary("Visiteurs actuellement présents sur le site.");

        // §7 — Sortie enregistrée MANUELLEMENT par la sûreté, sans scan.
        // Cas réel : le visiteur repart sans présenter son QR (téléphone
        // déchargé, autre accès, oubli). Sans cette action il resterait
        // « présent » indéfiniment et continuerait à générer des alertes de
        // dépassement que personne ne peut clore.
        group.MapPost("/on-site/{visitId:guid}/check-out", async (
            Guid visitId,
            ClaimsPrincipal user,
            IVisitRepository visits,
            IScanLogRepository logs,
            IScanEventBroadcaster broadcaster,
            IHostDirectory hosts,
            INotificationService notifications,
            IAdminAuditLog audit,
            IDateTimeProvider clock,
            CancellationToken ct) =>
        {
            var visit = await visits.GetByIdAsync(visitId, ct);
            if (visit is null)
                return Results.NotFound(new { error = "Demande introuvable." });

            var now = clock.UtcNow;
            var actor = user.HostIdentifier();
            var outcome = visit.ForceCheckOut(now);

            if (!outcome.IsCheckOut)
                return Results.Conflict(new { error = "Ce visiteur n'est pas enregistré comme présent." });

            // Journalisée comme toute sortie, mais explicitement attribuée à
            // l'agent de sûreté et signalée comme manuelle : le journal doit
            // permettre de distinguer une sortie constatée au poste d'une
            // sortie déclarée depuis le dashboard.
            await logs.AddAsync(ScanLogEntry.Create(
                visit.Id, visit.VisitorName, actor, CheckpointDirection.Exit,
                outcome, degradedMode: false,
                $"Sortie enregistrée manuellement depuis le dashboard sûreté (sans scan)"
                + (outcome.OverstayMinutesAtCheckOut > 0
                    ? $" · dépassement +{outcome.OverstayMinutesAtCheckOut} min"
                    : string.Empty),
                now, ScanAuthMethod.DashboardOverride), ct);

            await visits.SaveChangesAsync(ct);

            await audit.RecordAsync(
                AdminAuditAction.ManualCheckOut, actor, visit.Id.ToString(),
                "Sortie enregistrée manuellement (visiteur reparti sans scanner).", ct);

            try
            {
                await broadcaster.BroadcastAsync(new ScanBroadcastEvent(
                    visit.Id, visit.VisitorName, "CHECKED_OUT", true, true, false, actor, now), ct);

                var host = await hosts.FindAsync(visit.HostUserId, ct);
                if (host is not null)
                    await notifications.NotifyHostAsync(new HostEventNotification(
                        HostEventKind.Departure, visit.Id, visit.VisitorName, host, now,
                        PresenceMinutes: outcome.PresenceMinutesAtCheckOut,
                        OverstayMinutes: outcome.OverstayMinutesAtCheckOut), ct);
            }
            catch
            {
                // Best-effort : la sortie est enregistrée, c'est l'essentiel.
            }

            return Results.Ok(new ManualCheckOutResponseDto(
                visit.Id, visit.VisitorName,
                outcome.PresenceMinutesAtCheckOut, outcome.OverstayMinutesAtCheckOut));
        })
        .WithName("ManualCheckOut")
        .WithSummary("Enregistre manuellement la sortie d'un visiteur présent (sans scan).");

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

            // Histogramme horaire (24 tranches, heure locale) — courbe d'affluence.
            var hourly = new int[24];
            foreach (var e in today)
                hourly[e.Timestamp.ToLocalTime().Hour]++;

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
                Recommendation: recommendation,
                HourlyScans: hourly);

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
        sb.AppendLine("Horodatage;Visiteur;Agent;Direction;Autorise;Sortie;EvenementSecurite;Detail;MethodeAcces");
        foreach (var e in entries)
        {
            sb.Append(e.Timestamp.ToString("o", CultureInfo.InvariantCulture)).Append(';')
              .Append(Csv(e.VisitorName)).Append(';')
              .Append(Csv(e.AgentId)).Append(';')
              .Append(e.Direction).Append(';')
              .Append(e.WasGranted ? "oui" : "non").Append(';')
              .Append(e.WasCheckOut ? "oui" : "non").Append(';')
              .Append(e.IsSecurityEvent ? "oui" : "non").Append(';')
              .Append(Csv(e.Detail)).Append(';')
              .Append(e.AuthMethod).AppendLine();
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
