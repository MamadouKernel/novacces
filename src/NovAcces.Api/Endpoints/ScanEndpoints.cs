using System.Security.Claims;
using NovAcces.Application.Visits;
using NovAcces.Domain.Enums;
using NovAcces.Shared.Auth;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Endpoints;

public static class ScanEndpoints
{
    public static RouteGroupBuilder MapScanEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scan").WithTags("Scan");

        group.MapPost("/", async (
            ScanRequestDto request,
            ClaimsPrincipal user,
            ScanQrHandler handler,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<CheckpointDirection>(request.Direction, ignoreCase: true, out var direction))
                return Results.BadRequest(new { error = "Direction invalide : 'Entry' ou 'Exit' attendu." });

            // Jalon 2 : IsBusinessDayOverride sera calculé par un IBusinessDayService
            // (jours fériés ivoiriens paramétrables par site) plutôt que codé en dur.
            var isBusinessDay = DateTimeOffset.UtcNow.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

            // L'agent est identifié par le terminal enrôlé (claim), pas par une
            // valeur du corps de requête : traçabilité fiable du poste de contrôle.
            var agentId = user.FindFirstValue(ClaimTypes.Name) ?? "terminal-inconnu";

            var command = new ScanQrCommand(
                request.SignedQrPayload, direction, agentId,
                IsDegradedMode: false, // en jalon 2 : distinction scan temps réel / resynchronisation différée
                isBusinessDay);

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
