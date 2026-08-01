using System.Security.Claims;
using NovAcces.Application.Abstractions;
using NovAcces.Application.Visits;
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
    /// <summary>
    /// Plafond du lot de resynchronisation. Chaque élément déclenche une
    /// transaction et une écriture INEFFAÇABLE au journal : un lot non borné
    /// est à la fois un déni de service et un moyen de noyer le journal.
    /// Dimensionné très au-dessus d'une coupure réaliste (quelques dizaines de
    /// scans) ; un terminal qui en aurait davantage enverra plusieurs lots.
    /// </summary>
    internal const int MaxResyncBatchSize = 200;

    public static RouteGroupBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/agent").WithTags("Agent")
            .RequireAuthorization(NovAccesPolicies.AgentTerminal);

        // Prise de poste : l'agent s'identifie (matricule + PIN) sur le terminal
        // déjà authentifié. Vérification serveur → jeton de poste signé, à joindre
        // aux scans pour les tracer à CET agent (traçabilité individuelle, §8.5).
        group.MapPost("/shift/start", async (
            ShiftStartRequestDto request,
            IAgentDirectory agents,
            IJwtTokenService jwt,
            ICurrentTenant tenant,
            ClaimsPrincipal user,
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
            var terminalId = user.FindFirstValue(NovAccesClaimTypes.TerminalId);
            if (!Guid.TryParse(terminalId, out var parsedTerminalId))
                return Results.Forbid();

            var (token, expiresAt) = jwt.CreateShiftToken(agent.Matricule, agent.DisplayName, tenant.SiteId, parsedTerminalId);

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
                v.VisitorName, StatusLabel(v, now), WindowStart(v), WindowEnd(v))).ToList();

            return Results.Ok(dto);
        })
        .WithName("ExpectedToday")
        .WithSummary("Liste des visiteurs attendus aujourd'hui (moindre privilège).");

        // §6 — Liste hors-ligne signée (TTL 4h).
        group.MapGet("/offline-list", async (
            IVisitRepository visits, IQrSigningService signing, IExclusionListService exclusions,
            IDateTimeProvider clock, CancellationToken ct) =>
        {
            var issuedAt = clock.UtcNow;
            var expiresAt = issuedAt.AddHours(4);
            var today = await visits.GetTodayActiveVisitsAsync(issuedAt, ct);

            // Exclusion évaluée à l'ÉMISSION de la liste, pas figée à la création
            // de la visite : une personne écartée entre-temps doit être refusée
            // aussi en mode dégradé (REQ-F-11).
            var excluded = await exclusions.GetExcludedNormalizedNamesAsync(ct);

            var entries = today
                .Select(v => new OfflineListEntry(
                    v.Id, v.VisitToken, v.ScheduledAt, IsExcluded(v, excluded), v.IsOnSite))
                .ToList();

            var signed = signing.SignDailyOfflineList(entries, issuedAt, expiresAt);
            return Results.Ok(new OfflineListDto(signed, issuedAt, expiresAt, entries.Count));
        })
        .WithName("OfflineList")
        .WithSummary("Liste des QR valides du jour, signée, pour le mode dégradé.");

        // §6.5 — Resynchronisation : confronte les scans hors-ligne au registre.
        //
        // Le verdict remonté par le terminal n'est JAMAIS pris pour argent
        // comptant : chaque scan est rejoué par ScanQrHandler, qui revérifie la
        // signature ES256 du QR et réapplique toute la règle métier (exclusion,
        // fenêtre, anti-rejeu, révocation). Le registre central est l'autorité,
        // le terminal n'est qu'un rapporteur. Sans cela, une clé de terminal
        // volée permettrait d'inscrire des « accès accordé » arbitraires dans un
        // journal que les triggers append-only rendent ineffaçable.
        //
        // Le champ WasGranted du terminal ne sert plus qu'à UNE chose : détecter
        // un écart entre ce qui a été décidé au poste pendant la coupure et ce
        // que le serveur aurait décidé — c'est précisément la définition d'un
        // conflit (§6.5), remonté à la sûreté.
        group.MapPost("/resync", async (
            ResyncRequestDto request,
            ClaimsPrincipal user,
            ICurrentTenant tenant,
            IJwtTokenService jwt,
            HttpRequest http,
            IQrSigningService signing,
            ScanQrHandler handler,
            IScanEventBroadcaster broadcaster,
            IBusinessDayService businessDays,
            IDateTimeProvider clock,
            CancellationToken ct) =>
        {
            if (request.Scans is null || request.Scans.Count == 0)
                return Results.BadRequest(new { error = "Le lot de resynchronisation ne peut pas être vide." });

            if (request.Scans.Count > MaxResyncBatchSize)
                return Results.BadRequest(new
                {
                    error = $"Lot trop volumineux ({MaxResyncBatchSize} scans maximum par envoi)."
                });

            // Traçabilité individuelle (§8.5) : matricule du jeton de poste si
            // présent, sinon le terminal.
            var terminalId = Guid.TryParse(user.FindFirstValue(NovAccesClaimTypes.TerminalId), out var parsed)
                ? parsed : (Guid?)null;
            var shift = jwt.ValidateShiftToken(http.Headers["X-Shift-Token"].ToString(), tenant.SiteId, terminalId);
            var agentId = shift?.Matricule ?? user.FindFirstValue(ClaimTypes.Name) ?? "terminal-inconnu";

            var isBusinessDay = businessDays.IsBusinessDay(clock.UtcNow);
            var conflicts = new List<ResyncConflictDto>();

            foreach (var scan in request.Scans)
            {
                var direction = Enum.TryParse<CheckpointDirection>(scan.Direction, true, out var d)
                    ? d : CheckpointDirection.Entry;

                // Le jeton de visite fiable vient de la SIGNATURE, jamais du
                // champ VisitToken fourni par le terminal.
                var verification = signing.VerifySignedToken(scan.SignedQrPayload ?? string.Empty);

                // Rejoue le scan : journalisation en mode dégradé incluse (§6.2 /
                // REQ-F-07 — chaque tentative hors ligne est inscrite au registre,
                // accordée comme refusée).
                var result = await handler.HandleAsync(new ScanQrCommand(
                    scan.SignedQrPayload ?? string.Empty, direction, agentId,
                    IsDegradedMode: true, isBusinessDay), ct);

                // Conflit : le poste a laissé passer hors ligne ce que le serveur
                // refuse. C'est un événement de sécurité à remonter à la sûreté.
                if (scan.WasGranted && !result.IsGranted)
                {
                    var reason = result.VerdictCode == "INVALID_SIGNATURE"
                        ? "Accès accordé hors ligne à un QR que le serveur ne reconnaît pas"
                        : $"Accès accordé hors ligne, refusé par le registre ({result.VerdictCode})";

                    conflicts.Add(new ResyncConflictDto(
                        verification.VisitToken ?? Guid.Empty,
                        result.VisitorName ?? "QR inconnu",
                        reason,
                        scan.OccurredAt));
                }
            }

            foreach (var c in conflicts)
                await broadcaster.BroadcastAsync(new ScanBroadcastEvent(
                    Guid.Empty, c.VisitorName, "RESYNC_CONFLICT", false, false, true, agentId, c.OccurredAt), ct);

            return Results.Ok(new ResyncResultDto(request.Scans.Count, conflicts));
        })
        .RequireRateLimiting("sensitive")
        .WithName("Resync")
        .WithSummary("Rejoue les scans hors-ligne contre le registre central et remonte les écarts.");

        return group;
    }

    /// <summary>
    /// Exclusion effective d'une visite : instantané figé à la création OU
    /// présence courante sur la liste du site. Même règle que Visit.Scan côté
    /// domaine — les deux doivent rester alignées.
    /// </summary>
    internal static bool IsExcluded(Visit v, IReadOnlySet<string> excludedNormalizedNames) =>
        v.IsExcluded || excludedNormalizedNames.Contains(ExclusionEntry.Normalize(v.VisitorName));

    /// <summary>
    /// Statut affiché à l'agent (§11) : attendu / sur site / sorti / révoqué /
    /// non venu. « Non venu » = la fenêtre de validité est passée et la
    /// personne n'est jamais entrée — c'est ce qui permet à l'agent de
    /// distinguer un visiteur encore attendu d'un rendez-vous manqué.
    /// </summary>
    private static string StatusLabel(Visit v, DateTimeOffset now) => v switch
    {
        { Status: VisitStatus.Revoked } => "révoqué",
        { IsOnSite: true } => "sur site",
        { HasCompletedCycle: true } => "sorti",
        { CheckedOutAt: not null } => "sorti",
        _ when WindowEnd(v) is { } end && now > end && v.CheckedInAt is null => "non venu",
        _ => "attendu",
    };

    private static DateTimeOffset? WindowStart(Visit v) =>
        v.Mode == AccessMode.Unique && v.ScheduledAt is { } s ? s.AddMinutes(-20) : null;

    private static DateTimeOffset? WindowEnd(Visit v) =>
        v.Mode == AccessMode.Unique && v.ScheduledAt is { } s ? s.AddMinutes(15) : null;
}
