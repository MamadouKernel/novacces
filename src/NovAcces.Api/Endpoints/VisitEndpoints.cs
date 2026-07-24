using System.Security.Claims;
using NovAcces.Application.Abstractions;
using NovAcces.Application.Visits;
using NovAcces.Domain.Enums;
using NovAcces.Shared.Auth;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Endpoints;

public static class VisitEndpoints
{
    public static RouteGroupBuilder MapVisitEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/visits").WithTags("Visits");

        // Liste des demandes de l'hôte connecté (REQ-F-09 : support de la révocation).
        group.MapGet("/mine", async (
            ClaimsPrincipal user,
            IVisitRepository visits,
            int? limit,
            CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var mine = await visits.GetByHostAsync(user.HostIdentifier(), take, ct);

            var dto = mine.Select(v => new HostVisitDto(
                v.Id, v.VisitorName, v.VisitorCompany, v.Motif, v.Mode.ToString(), v.Status.ToString(),
                v.ScheduledAt, v.PlannedDurationMinutes, v.IsOnSite, v.CreatedAt)).ToList();

            return Results.Ok(dto);
        })
        .RequireAuthorization(NovAccesRoles.Hote)
        .WithName("MyVisits")
        .WithSummary("Liste les demandes de visite créées par l'hôte connecté.");

        // Autocomplétion des visiteurs déjà connus du site (maquette du 22/07/2026),
        // avec pré-remplissage entreprise/motif/durée.
        group.MapGet("/known-visitors", async (IVisitRepository visits, CancellationToken ct) =>
        {
            var known = await visits.GetKnownVisitorsAsync(500, ct);
            var dto = known.Select(k => new KnownVisitorDto(k.Name, k.Company, k.Motif, k.PlannedDurationMinutes)).ToList();
            return Results.Ok(dto);
        })
        .RequireAuthorization(NovAccesRoles.Hote)
        .WithName("KnownVisitors")
        .WithSummary("Noms de visiteurs déjà connus du site (autocomplétion).");

        group.MapPost("/", async (
            CreateVisitRequestDto request,
            ClaimsPrincipal user,
            IVisitRepository visits,
            CreateVisitHandler handler,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<AccessMode>(request.Mode, ignoreCase: true, out var mode))
                return Results.BadRequest(new { error = "Mode invalide : 'Unique' ou 'ThirtyDays' attendu." });

            if (string.IsNullOrWhiteSpace(request.VisitorName))
                return Results.BadRequest(new { error = "Nom du visiteur requis." });

            // Garde-fou anti-doublon (maquette du 22/07/2026) : une seule demande
            // active par visiteur sur le site.
            if (await visits.HasActiveVisitForVisitorAsync(request.VisitorName, ct))
                return Results.Conflict(new { error = "Une demande active existe déjà pour ce visiteur." });

            var command = new CreateVisitCommand(
                request.VisitorName, request.VisitorCompany, request.Motif,
                HostUserId: user.HostIdentifier(), // hôte authentifié (claim), plus de valeur en dur
                mode, request.ScheduledAt, request.PlannedDurationMinutes,
                request.VisitorPhone, request.VisitorEmail);

            var result = await handler.HandleAsync(command, ct);

            return Results.Ok(new CreateVisitResponseDto(result.VisitId, result.SignedQrPayload, result.ExpiresAt));
        })
        .RequireAuthorization(NovAccesRoles.Hote)
        .WithName("CreateVisit")
        .WithSummary("Crée une demande de visite et génère le QR signé.");

        // REQ-F-09 : révocation manuelle par l'hôte ou la sûreté, à tout moment.
        group.MapPost("/{visitId:guid}/revoke", async (
            Guid visitId,
            ClaimsPrincipal user,
            RevokeVisitHandler handler,
            CancellationToken ct) =>
        {
            // Moindre privilège (section 8.5) : Sûreté/Admin révoquent tout QR du
            // site ; un Hôte uniquement ses propres demandes (vérifié dans le handler).
            var canRevokeAny = user.IsInRole(NovAccesRoles.Surete) || user.IsInRole(NovAccesRoles.Admin);

            var result = await handler.HandleAsync(
                new RevokeVisitCommand(visitId, user.HostIdentifier(), user.HostIdentifier(), canRevokeAny), ct);

            if (result.Forbidden)
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status403Forbidden);

            return result.Success
                ? Results.Ok(new { message = "QR révoqué." })
                : Results.NotFound(new { error = result.Error });
        })
        .RequireAuthorization("RevokeVisit")
        .WithName("RevokeVisit")
        .WithSummary("Révoque un QR à tout moment (REQ-F-09).");

        return group;
    }
}

internal static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Identifiant stable de l'utilisateur pour journalisation/propriété
    /// (sub du JWT, à défaut le nom). Jamais une valeur fournie par le client.
    /// </summary>
    public static string HostIdentifier(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? user.FindFirstValue(ClaimTypes.Name)
        ?? "inconnu";
}
