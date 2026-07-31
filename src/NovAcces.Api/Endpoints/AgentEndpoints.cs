using System.Security.Claims;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;
using NovAcces.Shared.Auth;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Endpoints;

/// <summary>
/// Endpoints consommés par le client mobile React Native, authentifié par jeton Agent
/// de terminal (rôle Agent). Fournissent : la liste des attendus du jour (§11,
/// moindre privilège), la liste hors-ligne signée (§6), et la resynchronisation
/// des scans effectués hors ligne (§6.5).
/// </summary>
public static class AgentEndpoints
{
    public static RouteGroupBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/agent").WithTags("Agent")
            .RequireAuthorization(NovAccesRoles.Agent);

        // Prise de poste : l'agent s'identifie (matricule + PIN) sur le terminal
        // déjà authentifié. Vérification serveur → jeton de poste signé, à joindre
        // aux scans pour les tracer à CET agent (traçabilité individuelle, §8.5).
        group.MapPost("/shift/start", async (
            ShiftStartRequestDto request,
            IAgentDirectory agents,
            IJwtTokenService jwt,
            ICurrentTenant tenant,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Matricule) || string.IsNullOrWhiteSpace(request.Pin))
                return Results.BadRequest(new { error = "Matricule et PIN requis." });

            var agent = await agents.VerifyAsync(request.Matricule, request.Pin, ct);
            if (agent is null)
                return Results.Json(new { error = "Matricule ou PIN incorrect." }, statusCode: StatusCodes.Status401Unauthorized);

            // Le site vient du tenant déjà résolu par TenantResolutionMiddleware
            // (claim SiteId du terminal, ou en-tête X-Site-Id revalidé contre la
            // liste AllowedSite pour un terminal partagé) — jamais relu du claim
            // brut, qui peut être absent pour un terminal multi-sites.
            var (token, expiresAt) = jwt.CreateShiftToken(agent.Matricule, agent.DisplayName, tenant.SiteId);

            return Results.Ok(new ShiftStartResponseDto(agent.Matricule, agent.DisplayName, token, expiresAt));
        })
        .RequireRateLimiting("sensitive")
        .WithName("ShiftStart")
        .WithSummary("Prise de poste : identifie l'agent (matricule + PIN) et ouvre un poste.");

        // Sites que CE terminal est autorisé à servir — alimente le sélecteur de
        // site à la prise de poste (un seul site : aucun sélecteur affiché côté
        // agent). Ne lit que les claims du principal, aucune donnée de tenant.
        group.MapGet("/sites", (ClaimsPrincipal user) =>
        {
            var single = user.FindFirstValue(NovAccesClaimTypes.SiteId);
            var sites = !string.IsNullOrWhiteSpace(single)
                ? new List<string> { single }
                : user.FindAll(NovAccesClaimTypes.AllowedSite).Select(c => c.Value).Distinct().ToList();

            return Results.Ok(sites);
        })
        .WithName("AgentTerminalSites")
        .WithSummary("Sites que ce terminal est autorisé à servir (choix du site si plusieurs).");

        // §11 — Attendus du jour : nom + statut + fenêtre UNIQUEMENT.
        group.MapGet("/expected-today", async (IVisitRepository visits, IDateTimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.UtcNow;
            var today = await visits.GetTodayActiveVisitsAsync(now, ct);

            var dto = today.Select(v => new ExpectedVisitorDto(
                v.VisitorName, StatusLabel(v), WindowStart(v), WindowEnd(v))).ToList();

            return Results.Ok(dto);
        })
        .WithName("ExpectedToday")
        .WithSummary("Liste des visiteurs attendus aujourd'hui (moindre privilège).");

        // §6 — Liste hors-ligne signée (TTL 4h).
        group.MapGet("/offline-list", async (
            IVisitRepository visits, IQrSigningService signing, IDateTimeProvider clock, CancellationToken ct) =>
        {
            var issuedAt = clock.UtcNow;
            var expiresAt = issuedAt.AddHours(4);
            var today = await visits.GetTodayActiveVisitsAsync(issuedAt, ct);

            var entries = today
                .Select(v => new OfflineListEntry(v.Id, v.VisitToken, v.ScheduledAt, v.IsExcluded, v.IsOnSite))
                .ToList();

            var signed = signing.SignDailyOfflineList(entries, issuedAt, expiresAt);
            return Results.Ok(new OfflineListDto(signed, issuedAt, expiresAt, entries.Count));
        })
        .WithName("OfflineList")
        .WithSummary("Liste des QR valides du jour, signée, pour le mode dégradé.");

        // §6.5 — Resynchronisation : confronte les scans hors-ligne au registre.
        group.MapPost("/resync", async (
            ResyncRequestDto request,
            ClaimsPrincipal user,
            IVisitRepository visits,
            IScanLogRepository logs,
            IScanEventBroadcaster broadcaster,
            IDateTimeProvider clock,
            CancellationToken ct) =>
        {
            var agentId = user.FindFirstValue(ClaimTypes.Name) ?? "terminal-inconnu";
            var conflicts = new List<ResyncConflictDto>();

            foreach (var scan in request.Scans)
            {
                var visit = await visits.GetByTokenAsync(scan.VisitToken, ct);
                var visitorName = visit?.VisitorName ?? "QR inconnu";
                var direction = Enum.TryParse<CheckpointDirection>(scan.Direction, true, out var d)
                    ? d : CheckpointDirection.Entry;

                // Conflit : un accès accordé hors ligne pour un QR entre-temps
                // révoqué (ou inconnu) = événement de sécurité à remonter.
                var isConflict = scan.WasGranted && (visit is null || visit.Status == VisitStatus.Revoked);

                // §6.2 / REQ-F-07 : CHAQUE scan hors-ligne est journalisé au registre
                // central, marqué mode dégradé — accordé, refusé, ou conflit —, et
                // pas seulement les conflits.
                ScanOutcome outcome;
                string detail;
                if (isConflict)
                {
                    var reason = visit is null
                        ? "QR inconnu confronté à la resynchronisation"
                        : "Accès accordé hors ligne à un QR révoqué pendant la coupure";
                    outcome = ScanOutcome.Denied(ScanDenialReason.Revoked, isSecurityEvent: true);
                    detail = $"Conflit de resynchronisation : {reason}";
                    conflicts.Add(new ResyncConflictDto(scan.VisitToken, visitorName, reason, scan.OccurredAt));
                }
                else if (scan.WasGranted)
                {
                    outcome = direction == CheckpointDirection.Exit
                        ? ScanOutcome.CheckedOut(0)
                        : ScanOutcome.Granted();
                    detail = "Scan hors ligne confronté (accès accordé).";
                }
                else
                {
                    // Refus hors-ligne (fenêtre, exclusion, expiration, signature…).
                    // Le motif local est repris tel quel ; « Expired » n'ayant pas de
                    // ScanDenialReason dédié est assimilé à TooLate (comme le domaine).
                    var reason = Enum.TryParse<ScanDenialReason>(scan.VerdictCode, out var r)
                        ? r : ScanDenialReason.TooLate;
                    outcome = ScanOutcome.Denied(reason, scan.WasSecurityEvent);
                    detail = $"Scan hors ligne confronté (refus : {scan.VerdictCode ?? "inconnu"}).";
                }

                await logs.AddAsync(ScanLogEntry.Create(
                    visit?.Id ?? Guid.Empty, visitorName, agentId, direction,
                    outcome, degradedMode: true, detail, clock.UtcNow), ct);
            }

            if (request.Scans.Count > 0)
                await logs.SaveChangesAsync(ct);

            // Les conflits (événements de sécurité) sont diffusés au dashboard sûreté.
            foreach (var c in conflicts)
                await broadcaster.BroadcastAsync(new ScanBroadcastEvent(
                    Guid.Empty, c.VisitorName, "RESYNC_CONFLICT", false, false, true, agentId, c.OccurredAt), ct);

            return Results.Ok(new ResyncResultDto(request.Scans.Count, conflicts));
        })
        .WithName("Resync")
        .WithSummary("Confronte les scans hors-ligne au registre et remonte les conflits.");

        return group;
    }

    private static string StatusLabel(Visit v) => v switch
    {
        { Status: VisitStatus.Revoked } => "révoqué",
        { IsOnSite: true } => "sur site",
        { HasCompletedCycle: true } => "sorti",
        _ => "attendu",
    };

    private static DateTimeOffset? WindowStart(Visit v) =>
        v.Mode == AccessMode.Unique && v.ScheduledAt is { } s ? s.AddMinutes(-20) : null;

    private static DateTimeOffset? WindowEnd(Visit v) =>
        v.Mode == AccessMode.Unique && v.ScheduledAt is { } s ? s.AddMinutes(15) : null;
}
