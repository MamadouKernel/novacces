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
            IAgentEventBroadcaster events,
            IDateTimeProvider clock,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<AccessMode>(request.Mode, ignoreCase: true, out var mode))
                return Results.BadRequest(new { error = "Mode invalide : 'Unique' ou 'ThirtyDays' attendu." });

            if (string.IsNullOrWhiteSpace(request.VisitorName))
                return Results.BadRequest(new { error = "Nom du visiteur requis." });

            // Garde-fou anti-doublon (maquette du 22/07/2026) : une seule demande
            // active par visiteur (nom + société) sur le site.
            if (await visits.HasActiveVisitForVisitorAsync(request.VisitorName, request.VisitorCompany, ct))
                return Results.Conflict(new { error = "Une demande active existe déjà pour ce visiteur." });

            var command = new CreateVisitCommand(
                request.VisitorName, request.VisitorCompany, request.Motif,
                HostUserId: user.HostIdentifier(), // hôte authentifié (claim), plus de valeur en dur
                mode, request.ScheduledAt, request.PlannedDurationMinutes,
                request.VisitorPhone, request.VisitorEmail);

            try
            {
                var result = await handler.HandleAsync(command, ct);
                await events.BroadcastVisitCreatedAsync(result.VisitId, request.VisitorName, clock.UtcNow, ct);
                return Results.Ok(new CreateVisitResponseDto(result.VisitId, result.SignedQrPayload, result.ExpiresAt));
            }
            catch (DuplicateActiveVisitException ex)
            {
                // Deux créations concurrentes du même visiteur : la vérification
                // ci-dessus les a toutes les deux laissées passer, la contrainte
                // base a tranché — même message que le garde-fou applicatif.
                return Results.Conflict(new { error = ex.Message });
            }
        })
        .RequireAuthorization(NovAccesRoles.Hote)
        .WithName("CreateVisit")
        .WithSummary("Crée une demande de visite et génère le QR signé.");

        // Création groupée : invite un lot de visiteurs en une opération.
        // Chaque ligne passe par les MÊMES règles que la création unitaire
        // (mode, nom requis, anti-doublon, exclusion, signature, notification).
        // Échecs isolés par ligne — un visiteur invalide n'annule pas les autres.
        group.MapPost("/bulk", async (
            BulkCreateVisitsRequestDto request,
            ClaimsPrincipal user,
            IVisitRepository visits,
            CreateVisitHandler handler,
            IAgentEventBroadcaster events,
            IDateTimeProvider clock,
            CancellationToken ct) =>
        {
            if (request.Visits is null || request.Visits.Count == 0)
                return Results.BadRequest(new { error = "Aucun visiteur à inviter." });

            if (request.Visits.Count > 100)
                return Results.BadRequest(new { error = "Lot trop volumineux (100 visiteurs maximum)." });

            var hostId = user.HostIdentifier();
            var items = new List<BulkCreateVisitItemDto>(request.Visits.Count);

            foreach (var r in request.Visits)
            {
                var name = r.VisitorName?.Trim() ?? "";
                try
                {
                    if (string.IsNullOrWhiteSpace(name))
                    { items.Add(Fail(name, "Nom du visiteur requis.")); continue; }

                    if (!Enum.TryParse<AccessMode>(r.Mode, ignoreCase: true, out var mode))
                    { items.Add(Fail(name, "Mode invalide.")); continue; }

                    if (mode == AccessMode.Unique && r.ScheduledAt is null)
                    { items.Add(Fail(name, "Date de rendez-vous requise (mode unique).")); continue; }

                    if (await visits.HasActiveVisitForVisitorAsync(name, r.VisitorCompany, ct))
                    { items.Add(Fail(name, "Une demande active existe déjà pour ce visiteur.")); continue; }

                    var command = new CreateVisitCommand(
                        name, r.VisitorCompany, r.Motif, hostId,
                        mode, r.ScheduledAt, r.PlannedDurationMinutes,
                        r.VisitorPhone, r.VisitorEmail);

                    var result = await handler.HandleAsync(command, ct);
                    await events.BroadcastVisitCreatedAsync(result.VisitId, name, clock.UtcNow, ct);
                    items.Add(new BulkCreateVisitItemDto(
                        name, true, result.VisitId, result.SignedQrPayload, result.ExpiresAt, null));
                }
                catch (DuplicateActiveVisitException)
                {
                    // Deux lignes du même lot pour le même visiteur (nom +
                    // société) ne peuvent normalement pas arriver ici — la
                    // boucle est séquentielle donc la vérification amont les
                    // aurait déjà distinguées — mais reste correct si jamais.
                    items.Add(Fail(name, "Une demande active existe déjà pour ce visiteur."));
                }
                catch
                {
                    items.Add(Fail(name, "Échec de la génération."));
                }
            }

            var created = items.Count(i => i.Success);
            return Results.Ok(new BulkCreateVisitsResponseDto(created, items.Count - created, items));

            static BulkCreateVisitItemDto Fail(string name, string error) =>
                new(name, false, null, null, null, error);
        })
        .RequireAuthorization(NovAccesRoles.Hote)
        .WithName("CreateVisitsBulk")
        .WithSummary("Crée un lot de demandes de visite (invitation groupée) et génère un QR par visiteur.");

        // Chronologie d'une demande (créée → RDV → entrée → sortie → révoquée).
        // Visible par la Sûreté/Admin pour toute demande ; par un Hôte, seulement
        // les siennes (moindre privilège).
        group.MapGet("/{visitId:guid}/history", async (
            Guid visitId,
            ClaimsPrincipal user,
            IVisitRepository visits,
            CancellationToken ct) =>
        {
            var visit = await visits.GetByIdAsync(visitId, ct);
            if (visit is null)
                return Results.NotFound(new { error = "Demande introuvable." });

            var canViewAny = NovAccesAuthorizationMatrix.CanViewAnyVisit(user);
            if (!canViewAny && visit.HostUserId != user.HostIdentifier())
                return Results.Json(new { error = "Accès refusé." }, statusCode: StatusCodes.Status403Forbidden);

            var events = new List<VisitEventDto>
            {
                new(visit.CreatedAt, "Demande créée",
                    $"{(visit.Mode == AccessMode.Unique ? "Passage unique" : "Accès 30 jours")} · durée prévue {visit.PlannedDurationMinutes} min",
                    "created"),
            };

            if (visit.ScheduledAt is { } scheduled)
                events.Add(new(scheduled, "Rendez-vous prévu", null, "scheduled"));

            if (visit.CheckedInAt is { } checkedIn)
                events.Add(new(checkedIn, "Entrée sur site", null, "entry"));

            if (visit.CheckedOutAt is { } checkedOut)
            {
                var over = visit.CheckedInAt is { } ci && visit.PlannedDurationMinutes > 0
                    ? Math.Max(0, (int)(checkedOut - ci).TotalMinutes - visit.PlannedDurationMinutes)
                    : 0;
                events.Add(new(checkedOut, "Sortie enregistrée",
                    over > 0 ? $"dépassement +{over} min" : null, "exit"));
            }

            if (visit.RevokedAt is { } revoked)
                events.Add(new(revoked, "QR révoqué", visit.RevokedBy, "revoked"));

            var ordered = events.OrderBy(e => e.At).ToList();
            return Results.Ok(new VisitHistoryDto(visit.Id, visit.VisitorName, visit.Status.ToString(), ordered));
        })
        .RequireAuthorization(NovAccesPolicies.DashboardApi)
        .WithName("VisitHistory")
        .WithSummary("Chronologie des statuts d'une demande de visite.");

        // Réémission du QR d'une demande existante : le visiteur a perdu le
        // message WhatsApp/email, ou son téléphone a changé — sans cette
        // route, la seule issue pour l'hôte était de révoquer et recréer la
        // demande. Resigne le MÊME jeton de visite (VisitToken inchangé) avec
        // la MÊME expiration que celle calculée à la création
        // (Visit.ComputeQrExpiry) : il ne s'agit pas d'un nouveau droit
        // d'accès, seulement de la réimpression d'un badge déjà émis.
        group.MapGet("/{visitId:guid}/qr", async (
            Guid visitId,
            ClaimsPrincipal user,
            IVisitRepository visits,
            IQrSigningService signing,
            IDateTimeProvider clock,
            CancellationToken ct) =>
        {
            var visit = await visits.GetByIdAsync(visitId, ct);
            if (visit is null)
                return Results.NotFound(new { error = "Demande introuvable." });

            var canViewAny = NovAccesAuthorizationMatrix.CanViewAnyVisit(user);
            if (!canViewAny && visit.HostUserId != user.HostIdentifier())
                return Results.Json(new { error = "Accès refusé." }, statusCode: StatusCodes.Status403Forbidden);

            // Seule une demande encore UTILISABLE peut être réimprimée :
            //  - Status == Valid exclut Revoked, Expired, et Consumed (mode
            //    Unique déjà entré — le revoir n'aiderait pas, le titulaire
            //    est déjà sur site) ;
            //  - la vérification d'expiration cryptographique couvre le
            //    plafond de 30 jours calendaires du mode ThirtyDays, qui ne
            //    se traduit jamais par un changement de Status.
            var expiresAt = visit.ComputeQrExpiry();
            if (visit.Status != VisitStatus.Valid || clock.UtcNow > expiresAt)
                return Results.Json(
                    new { error = "Ce QR n'est plus disponible (révoqué, expiré, ou déjà utilisé)." },
                    statusCode: StatusCodes.Status409Conflict);

            var signedPayload = signing.SignVisitToken(visit.Id, visit.VisitToken, expiresAt);
            return Results.Ok(new CreateVisitResponseDto(visit.Id, signedPayload, expiresAt));
        })
        .RequireAuthorization(NovAccesPolicies.DashboardApi)
        .WithName("ReissueVisitQr")
        .WithSummary("Réémet le QR signé d'une demande encore valide (visiteur ayant perdu son message).");

        // REQ-F-09 : révocation manuelle par l'hôte ou la sûreté, à tout moment.
        group.MapPost("/{visitId:guid}/revoke", async (
            Guid visitId,
            ClaimsPrincipal user,
            RevokeVisitHandler handler,
            IAgentEventBroadcaster events,
            IDateTimeProvider clock,
            CancellationToken ct) =>
        {
            // Moindre privilège (section 8.5) : Sûreté/Admin révoquent tout QR du
            // site ; un Hôte uniquement ses propres demandes (vérifié dans le handler).
            var canRevokeAny = NovAccesAuthorizationMatrix.CanRevokeAnyVisit(user);

            var result = await handler.HandleAsync(
                new RevokeVisitCommand(visitId, user.HostIdentifier(), user.HostIdentifier(), canRevokeAny), ct);

            if (result.Forbidden)
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status403Forbidden);

            if (result.Success)
                await events.BroadcastVisitRevokedAsync(visitId, clock.UtcNow, ct);

            return result.Success
                ? Results.Ok(new { message = "QR révoqué." })
                : Results.NotFound(new { error = result.Error });
        })
        .RequireAuthorization(NovAccesPolicies.RevokeVisit)
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
