using NovAcces.Application.Visits;
using NovAcces.Domain.Enums;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Endpoints;

public static class VisitEndpoints
{
    public static RouteGroupBuilder MapVisitEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/visits").WithTags("Visits");

        group.MapPost("/", async (
            CreateVisitRequestDto request,
            CreateVisitHandler handler,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<AccessMode>(request.Mode, ignoreCase: true, out var mode))
                return Results.BadRequest(new { error = "Mode invalide : 'Unique' ou 'ThirtyDays' attendu." });

            // Jalon 2 : garde-fou anti-doublon (une seule demande active par
            // visiteur, cf. maquette de démonstration) à appliquer ici avant
            // l'appel au handler, via une requête de vérification dédiée.

            var command = new CreateVisitCommand(
                request.VisitorName, request.VisitorCompany, request.Motif,
                HostUserId: "TODO-jalon2-depuis-authentification", // remplacé par l'identité de l'hôte connecté
                mode, request.ScheduledAt, request.PlannedDurationMinutes,
                request.VisitorPhone, request.VisitorEmail);

            var result = await handler.HandleAsync(command, ct);

            return Results.Ok(new CreateVisitResponseDto(result.VisitId, result.SignedQrPayload, result.ExpiresAt));
        })
        .WithName("CreateVisit")
        .WithSummary("Crée une demande de visite et génère le QR signé.");

        // REQ-F-09 : révocation manuelle par l'hôte ou la sûreté, à tout moment.
        group.MapPost("/{visitId:guid}/revoke", async (
            Guid visitId,
            RevokeVisitHandler handler,
            CancellationToken ct) =>
        {
            // TODO Jalon 2 : remplacer "TODO-jalon2" par l'identité réelle de
            // l'utilisateur authentifié (hôte propriétaire de la visite, ou
            // responsable sûreté) une fois Identity + RBAC en place. Vérifier
            // aussi qu'un hôte ne peut révoquer QUE ses propres demandes
            // (principe du moindre privilège, section 8.5 du CDC).
            var result = await handler.HandleAsync(new RevokeVisitCommand(visitId, RevokedBy: "TODO-jalon2"), ct);

            return result.Success
                ? Results.Ok(new { message = "QR révoqué." })
                : Results.NotFound(new { error = result.Error });
        })
        .WithName("RevokeVisit")
        .WithSummary("Révoque un QR à tout moment (REQ-F-09).");

        return group;
    }
}
