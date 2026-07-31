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

            // Jour ouvré (REQ-F-05) : week-end + jours fériés du site.
            var isBusinessDay = businessDays.IsBusinessDay(clock.UtcNow);

            // Attribution du scan : si un jeton de poste valide est présent
            // (prise de poste matricule + PIN), on trace au MATRICULE de l'agent
            // (traçabilité individuelle, §8.5) ; sinon, repli sur le terminal.
            // Le site vient du tenant déjà résolu (claim du terminal, ou en-tête
            // X-Site-Id revalidé pour un terminal multi-sites) — jamais relu du
            // claim brut, absent pour un terminal partagé entre plusieurs sites.
            var siteId = tenant.SiteId;
            var shiftToken = http.Headers["X-Shift-Token"].ToString();
            var shift = string.IsNullOrWhiteSpace(shiftToken) ? null : jwt.ValidateShiftToken(shiftToken, siteId);
            var agentId = shift?.Matricule ?? user.FindFirstValue(ClaimTypes.Name) ?? "terminal-inconnu";

            var command = new ScanQrCommand(
                request.SignedQrPayload, direction, agentId,
                IsDegradedMode: false, // en jalon 2 : distinction scan temps réel / resynchronisation différée
                isBusinessDay,
                request.CheckpointId);

            var result = await handler.HandleAsync(command, ct);

            var response = new ScanResponseDto(
                result.IsGranted, result.IsCheckOut, result.IsSecurityEvent,
                result.VerdictCode, result.VisitorName, result.OverstayMinutes);

            return Results.Ok(response);
        })
        .RequireAuthorization(NovAccesRoles.Agent)
        .WithName("ScanQr")
        .WithSummary("Scanne un QR au poste de contrôle (entrée ou sortie).");

        return group;
    }
}
