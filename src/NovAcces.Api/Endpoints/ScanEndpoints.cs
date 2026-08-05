using System.Security.Claims;
using NovAcces.Application.Abstractions;
using NovAcces.Application.Visits;
using NovAcces.Domain.Enums;
using NovAcces.Shared.Auth;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Endpoints;

public static class ScanEndpoints
{
    public static RouteGroupBuilder MapScanEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scan")
            .WithTags("Scan")
            .AddEndpointFilter<ContractSiteHeaderFilter>();

        group.MapPost("/", async (
            ScanRequestDto request,
            ClaimsPrincipal user,
            HttpRequest http,
            ScanQrHandler handler,
            IJwtTokenService jwt,
            ITerminalDirectory terminals,
            IBusinessDayService businessDays,
            IDateTimeProvider clock,
            ICurrentTenant tenant,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<CheckpointDirection>(request.Direction, ignoreCase: true, out var direction))
                return Results.BadRequest(new { error = "Direction invalide : 'Entry' ou 'Exit' attendu." });

            var isBusinessDay = businessDays.IsBusinessDay(clock.UtcNow);
            var agentId = await AgentAttribution.ResolveAgentIdAsync(user, http, jwt, terminals, tenant, ct);

            var command = new ScanQrCommand(
                request.SignedQrPayload, direction, agentId,
                IsDegradedMode: false, // en jalon 2 : distinction scan temps réel / resynchronisation différée
                isBusinessDay,
                request.CheckpointId);

            var result = await handler.HandleAsync(command, ct);

            var response = new ScanResponseDto(
                result.IsGranted, result.IsCheckOut, result.IsSecurityEvent,
                result.VerdictCode, result.VisitorName, result.OverstayMinutes,
                result.PresenceMinutes);

            return Results.Ok(response);
        })
        .RequireAuthorization(NovAccesPolicies.AgentTerminal)
        .WithName("ScanQr")
        .WithSummary("Scanne un QR au poste de contrôle (entrée ou sortie).")
        .WithDescription(VerdictCodeDescription)
        .Produces<ScanResponseDto>(StatusCodes.Status200OK)
        .Produces<ErrorResponseDto>(StatusCodes.Status400BadRequest);

        // Alternative au QR : le visiteur donne un code de secours reçu par
        // email (téléphone déchargé, QR qui ne scanne pas). Mêmes règles de
        // sûreté que /api/scan (même groupe : hérite de la limite de débit
        // "sensitive" posée sur tout /api/scan), voir ScanExecutionCore.
        group.MapPost("/manual-code", async (
            ScanManualCodeRequestDto request,
            ClaimsPrincipal user,
            HttpRequest http,
            ScanManualCodeHandler handler,
            IJwtTokenService jwt,
            ITerminalDirectory terminals,
            IBusinessDayService businessDays,
            IDateTimeProvider clock,
            ICurrentTenant tenant,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<CheckpointDirection>(request.Direction, ignoreCase: true, out var direction))
                return Results.BadRequest(new { error = "Direction invalide : 'Entry' ou 'Exit' attendu." });
            if (string.IsNullOrWhiteSpace(request.Code))
                return Results.BadRequest(new { error = "Code requis." });

            var isBusinessDay = businessDays.IsBusinessDay(clock.UtcNow);
            var agentId = await AgentAttribution.ResolveAgentIdAsync(user, http, jwt, terminals, tenant, ct);

            var command = new ScanManualCodeCommand(
                request.Code, direction, agentId,
                IsDegradedMode: false, // le code de secours n'est pas résolvable hors ligne (§9 du plan) — toujours en ligne
                isBusinessDay,
                request.CheckpointId);

            var result = await handler.HandleAsync(command, ct);

            var response = new ScanResponseDto(
                result.IsGranted, result.IsCheckOut, result.IsSecurityEvent,
                result.VerdictCode, result.VisitorName, result.OverstayMinutes,
                result.PresenceMinutes);

            return Results.Ok(response);
        })
        .RequireAuthorization(NovAccesPolicies.AgentTerminal)
        .WithName("ScanManualCode")
        .WithSummary("Autorise l'accès via le code de secours (alternative au QR).")
        .WithDescription(VerdictCodeDescription
            + " Toujours en ligne (§9) : le code de secours n'est pas résolvable hors ligne.")
        .Produces<ScanResponseDto>(StatusCodes.Status200OK)
        .Produces<ErrorResponseDto>(StatusCodes.Status400BadRequest);

        return group;
    }

    // Énumération exhaustive de ScanResponseDto.VerdictCode (§Q4 retour app
    // agent, 05/08/2026) — un code inconnu ne doit jamais être présenté comme
    // une autorisation côté app.
    private const string VerdictCodeDescription =
        "verdictCode : GRANTED, CHECKED_OUT, INVALID_SIGNATURE, INVALID_CODE (manual-code "
        + "uniquement), ou DENIED_{motif} avec motif ∈ Excluded, NoActiveEntry, "
        + "SuspectedDuplicate, Revoked, CycleAlreadyClosed, AlreadyConsumed, TooEarly, "
        + "TooLate, NonBusinessDay.";
}
