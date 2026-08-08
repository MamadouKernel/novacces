using System.Security.Claims;
using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;
using NovAcces.Application.Visits;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;
using NovAcces.Infrastructure.Security;
using NovAcces.Infrastructure.Identity;
using NovAcces.Shared.Auth;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Endpoints;

/// <summary>
/// Routes stables du contrat React Native. Les routes historiques /api/agent/*
/// restent actives pour les intégrations historiques ; React Native utilise les routes contractuelles stables.
/// </summary>
public static class AgentContractEndpoints
{
    public static void MapAgentContractEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", () =>
        {
            var now = DateTimeOffset.UtcNow;
            return Results.Ok(new HealthResponseDto("ok", now, now));
        })
        .WithName("ApiHealth")
        .WithSummary("Vérifie la disponibilité de l'API et fournit l'heure serveur.")
        .Produces<HealthResponseDto>(StatusCodes.Status200OK);

        // Nécessaire avant connexion pour vérifier les listes signées hors ligne.
        // Les clés retirées encore acceptées sont incluses : sans elles, un
        // terminal en mode dégradé refuserait pendant une rotation les QR
        // longue durée signés avec l'ancienne clé.
        app.MapGet("/api/keys/public", (IOptions<QrSigningOptions> options) =>
            Results.Ok(new PublicKeyDto(
                options.Value.KeyId,
                options.Value.PublicKeyPem,
                options.Value.RetiredVerificationKeys
                    .Select(k => new PublicKeyEntryDto(k.KeyId, k.PublicKeyPem))
                    .ToList())))
            .WithName("PublicQrSigningKey")
            .WithSummary("Retourne la clé publique ES256 courante et les clés retirées encore acceptées.")
            .Produces<PublicKeyDto>(StatusCodes.Status200OK);

        var agent = app.MapGroup("/api")
            .RequireAuthorization(NovAccesPolicies.AgentTerminal)
            .AddEndpointFilter<ContractSiteHeaderFilter>()
            // Audit du 05/08/2026, finding F1 : /site/config et /offline-list
            // n'avaient aucune politique de débit (contrairement à /scan/sync
            // ci-dessous, qui la reprend donc désormais du groupe).
            .RequireRateLimiting("sensitive");

        agent.MapGet("/site/config", (ICurrentTenant tenant, IConfiguration configuration) =>
        {
            var siteId = tenant.SiteId;
            var siteLabel = configuration[$"Sites:{siteId}:Label"]
                ?? configuration["Agent:SiteLabel"]
                ?? siteId;

            var checkpoints = configuration.GetSection($"Sites:{siteId}:Checkpoints").GetChildren()
                .Select(x => new CheckpointDto(
                    x.Key,
                    x["Label"] ?? x.Key))
                .ToList();

            if (checkpoints.Count == 0)
                checkpoints.AddRange(new[]
                {
                    new CheckpointDto("entry", "Entrée"),
                    new CheckpointDto("exit", "Sortie"),
                });

            var before = configuration.GetValue("Agent:WindowBeforeMinutes", 20);
            var after = configuration.GetValue("Agent:WindowAfterMinutes", 15);
            var ttl = configuration.GetValue("Agent:OfflineListTtlHours", 4);
            return Results.Ok(new SiteConfigDto(siteLabel, checkpoints,
                new SiteParametersDto(before, after, ttl)));
        })
        .WithName("AgentSiteConfig")
        .WithSummary("Retourne la configuration du poste agent pour le site courant.")
        .WithDescription("checkpointId n'est PAS validé contre Postes[].Id — transporté tel quel, informatif.")
        .Produces<SiteConfigDto>(StatusCodes.Status200OK);

        agent.MapGet("/offline-list", async (
            IVisitRepository visits,
            IQrSigningService signing,
            IExclusionListService exclusions,
            IDateTimeProvider clock,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            var issuedAt = clock.UtcNow;
            var ttl = Math.Clamp(configuration.GetValue("Agent:OfflineListTtlHours", 4), 1, 4);
            var expiresAt = issuedAt.AddHours(ttl);
            var today = await visits.GetTodayActiveVisitsAsync(issuedAt, ct);

            // Exclusion évaluée à l'ÉMISSION, pas figée à la création (REQ-F-11).
            var excluded = await exclusions.GetExcludedNormalizedNamesAsync(ct);

            var entries = today.Select(v => new OfflineListEntry(
                v.Id, v.VisitToken, v.ScheduledAt, AgentEndpoints.IsExcluded(v, excluded), v.IsOnSite,
                v.VisitorName, v.Mode.ToString(),
                v.ScheduledAt?.AddMinutes(-20), v.ScheduledAt?.AddMinutes(15), v.Status.ToString())).ToList();
            var signed = signing.SignDailyOfflineList(entries, issuedAt, expiresAt);
            var visitsDto = today.Select(v => new ContractOfflineVisitDto(
                v.Id, v.VisitorName, v.Mode.ToString(),
                v.ScheduledAt?.AddMinutes(-20), v.ScheduledAt?.AddMinutes(15),
                v.Status.ToString(), v.IsOnSite, v.ComputeOverstayMinutes(issuedAt))).ToList();
            return Results.Ok(new ContractOfflineListDto(issuedAt, expiresAt, visitsDto, signed));
        })
        .WithName("ContractOfflineList")
        .WithSummary("Sert la liste quotidienne signée, avec un TTL de quatre heures maximum.")
        .WithDescription(
            "SignedList est une chaîne JSON (pas un JWS compact RFC 7515) : "
            + "{\"PayloadB64Url\":...,\"SignatureB64Url\":...,\"KeyId\":...} — "
            + "à parser une seconde fois après la désérialisation de la réponse HTTP.")
        .Produces<ContractOfflineListDto>(StatusCodes.Status200OK);

        agent.MapPost("/scan/sync", async (
            IReadOnlyList<ContractOfflineScanDto> request,
            ClaimsPrincipal user,
            ICurrentTenant tenant,
            IJwtTokenService jwt,
            ITerminalDirectory terminals,
            HttpRequest http,
            IQrSigningService signing,
            ScanQrHandler handler,
            IBusinessDayService businessDays,
            IDateTimeProvider clock,
            CancellationToken ct) =>
        {
            if (request is null || request.Count == 0)
                return Results.BadRequest(new { error = "Le lot de resynchronisation ne peut pas être vide." });

            // Même plafond que /api/agent/resync : chaque élément écrit une ligne
            // ineffaçable au journal, un lot non borné est un DoS doublé d'un
            // moyen de noyer la traçabilité.
            if (request.Count > AgentEndpoints.MaxResyncBatchSize)
                return Results.BadRequest(new
                {
                    error = $"Lot trop volumineux ({AgentEndpoints.MaxResyncBatchSize} scans maximum par envoi)."
                });

            var agentId = await AgentAttribution.ResolveAgentIdAsync(user, http, jwt, terminals, tenant, ct);
            var conflicts = new List<ContractSyncConflictDto>();
            var isBusinessDay = businessDays.IsBusinessDay(clock.UtcNow);

            foreach (var scan in request)
            {
                if (!Enum.TryParse<CheckpointDirection>(scan.Direction, true, out var direction))
                    return Results.BadRequest(new { error = "Direction invalide : 'Entry' ou 'Exit' attendu." });

                var verification = signing.VerifySignedToken(scan.SignedQrPayload);
                var result = await handler.HandleAsync(new ScanQrCommand(
                    scan.SignedQrPayload, direction, agentId,
                    IsDegradedMode: true, isBusinessDay, CheckpointId: null), ct);

                var verifiedVisitId = verification.VisitId;
                if (IsOfflineGrant(scan.OfflineVerdict)
                    && !result.IsGranted
                    && verifiedVisitId.HasValue)
                {
                    conflicts.Add(new ContractSyncConflictDto(
                        verifiedVisitId.Value, $"Verdict serveur {result.VerdictCode} différent du verdict hors ligne."));
                }
            }

            var response = new ContractScanSyncResponse(request.Count - conflicts.Count, conflicts);
            return conflicts.Count == 0
                ? Results.Ok(response)
                : Results.Json(response, statusCode: StatusCodes.Status409Conflict);
        })
        .WithName("ContractScanSync")
        .WithSummary("Resynchronise un tableau de scans hors ligne en réutilisant le handler métier et la vérification JWS.")
        .WithDescription("Route stable pour l'app React Native (voir /api/agent/resync pour l'ancienne route MAUI).")
        .Produces<ContractScanSyncResponse>(StatusCodes.Status200OK)
        .Produces<ContractScanSyncResponse>(StatusCodes.Status409Conflict)
        .Produces<ErrorResponseDto>(StatusCodes.Status400BadRequest);
    }

    private static bool IsOfflineGrant(string verdict) =>
        verdict.StartsWith("GRANTED", StringComparison.OrdinalIgnoreCase)
        || verdict.StartsWith("CHECKED_OUT", StringComparison.OrdinalIgnoreCase);
}
