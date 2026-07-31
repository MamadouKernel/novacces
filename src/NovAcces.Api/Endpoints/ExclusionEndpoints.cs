using System.Security.Claims;
using NovAcces.Application.Abstractions;
using NovAcces.Application.Visits;
using NovAcces.Domain.Enums;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Endpoints;

/// <summary>
/// Gestion de la liste d'exclusion du site (REQ-F-11), réservée à la Sûreté et
/// l'Admin. Le motif n'est visible que d'eux (moindre privilège) ; l'agent, lui,
/// ne reçoit qu'un refus générique au scan d'un visiteur exclu.
/// </summary>
public static class ExclusionEndpoints
{
    public static RouteGroupBuilder MapExclusionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/exclusions").WithTags("Exclusions")
            .RequireAuthorization("ManageExclusions");

        group.MapGet("/", async (IExclusionListService exclusions, CancellationToken ct) =>
        {
            var list = await exclusions.ListAsync(ct);
            var dto = list.Select(e => new ExclusionDto(e.Id, e.DisplayName, e.Reason, e.AddedBy, e.CreatedAt)).ToList();
            return Results.Ok(dto);
        })
        .WithName("ListExclusions")
        .WithSummary("Liste d'exclusion du site (avec motifs).");

        group.MapPost("/", async (
            AddExclusionRequestDto request,
            ClaimsPrincipal user,
            IExclusionListService exclusions,
            IAdminAuditLog audit,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.Reason))
                return Results.BadRequest(new { error = "Nom et motif requis." });

            var entryId = await exclusions.AddAsync(request.DisplayName, request.Reason, user.HostIdentifier(), ct);

            // Traçabilité §8.5 : l'ajout à la liste d'exclusion est un paramétrage
            // de sûreté — inscrit au journal d'audit inaltérable. Minimisation :
            // on référence l'entrée par son identifiant opaque, sans recopier le
            // nom du visiteur (PII) dans le journal (le nom vit dans exclusion_entries,
            // consultable par la Sûreté tant que l'entrée existe).
            await audit.RecordAsync(
                AdminAuditAction.ExclusionAdded, user.HostIdentifier(), entryId.ToString(),
                "Ajout d'une entrée à la liste d'exclusion.", ct);

            return Results.Ok(new { message = "Ajouté à la liste d'exclusion." });
        })
        .WithName("AddExclusion")
        .WithSummary("Ajoute une personne à la liste d'exclusion.");

        group.MapPost("/{id:guid}/remove", async (
            Guid id,
            ClaimsPrincipal user,
            IExclusionListService exclusions,
            IAdminAuditLog audit,
            CancellationToken ct) =>
        {
            var removed = await exclusions.RemoveAsync(id, ct);
            if (!removed)
                return Results.NotFound();

            await audit.RecordAsync(
                AdminAuditAction.ExclusionRemoved, user.HostIdentifier(), id.ToString(),
                $"Retrait de la liste d'exclusion (entrée {id}).", ct);

            return Results.Ok(new { message = "Retiré." });
        })
        .WithName("RemoveExclusion")
        .WithSummary("Retire une personne de la liste d'exclusion.");

        return group;
    }
}
