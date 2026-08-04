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
            IBusinessDayService businessDays,
            IDateTimeProvider clock,
            ICurrentTenant tenant,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<CheckpointDirection>(request.Direction, ignoreCase: true, out var direction))
                return Results.BadRequest(new { error = "Direction invalide : 'Entry' ou 'Exit' attendu." });

            var isBusinessDay = businessDays.IsBusinessDay(clock.UtcNow);
            var agentId = ResolveAgentId(user, http, jwt, tenant);

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
        .WithSummary("Scanne un QR au poste de contrôle (entrée ou sortie).");

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
            var agentId = ResolveAgentId(user, http, jwt, tenant);

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
        .WithSummary("Autorise l'accès via le code de secours (alternative au QR).");

        return group;
    }

    // Attribution du scan : si un jeton de poste valide est présent (prise de
    // poste matricule + PIN), on trace au MATRICULE de l'agent (traçabilité
    // individuelle, §8.5) ; sinon, repli sur le terminal. Le site vient du
    // tenant déjà résolu (claim du terminal, ou en-tête X-Site-Id revalidé
    // pour un terminal multi-sites) — jamais relu du claim brut, absent pour
    // un terminal partagé entre plusieurs sites.
    private static string ResolveAgentId(
        ClaimsPrincipal user, HttpRequest http, IJwtTokenService jwt, ICurrentTenant tenant)
    {
        var siteId = tenant.SiteId;
        var shiftToken = http.Headers["X-Shift-Token"].ToString();
        var terminalId = Guid.TryParse(user.FindFirstValue(NovAccesClaimTypes.TerminalId), out var parsedTerminalId)
            ? parsedTerminalId : (Guid?)null;
        var shift = string.IsNullOrWhiteSpace(shiftToken)
            ? null
            : jwt.ValidateShiftToken(shiftToken, siteId, terminalId);
        return shift?.Matricule ?? user.FindFirstValue(ClaimTypes.Name) ?? "terminal-inconnu";
    }
}
