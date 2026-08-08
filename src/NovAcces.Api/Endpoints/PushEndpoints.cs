using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NovAcces.Infrastructure.Identity;
using NovAcces.Infrastructure.Notifications;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Endpoints;

/// <summary>
/// Abonnement WebPush (PWA) du navigateur — permet de réveiller un onglet
/// FERMÉ pour l'alerte de dépassement (§7), voir IOverstayPushNotifier.
/// Ouvert à tout utilisateur Web authentifié (Hôte, Sûreté, Admin,
/// SuperAdmin) : chacun ne gère QUE son propre abonnement.
/// </summary>
public static class PushEndpoints
{
    public static RouteGroupBuilder MapPushEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/push").WithTags("Push");

        group.MapGet("/vapid-public-key", (IOptions<WebPushOptions> options) =>
                Results.Ok(new VapidPublicKeyDto(options.Value.VapidPublicKey)))
            .AllowAnonymous()
            .WithName("VapidPublicKey")
            .WithSummary("Clé publique VAPID nécessaire à l'abonnement WebPush du navigateur.");

        group.MapPost("/subscribe", async (
            PushSubscriptionRequestDto request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> users,
            NovAccesIdentityDbContext db,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Endpoint)
                || string.IsNullOrWhiteSpace(request.Keys?.P256dh)
                || string.IsNullOrWhiteSpace(request.Keys?.Auth))
                return Results.BadRequest(new { error = "Abonnement incomplet (endpoint/clés requis)." });

            var user = await users.GetUserAsync(principal);
            if (user is null)
                return Results.Unauthorized();

            // Ré-abonnement (même navigateur) : remplace l'existant plutôt que
            // d'en accumuler un doublon — l'Endpoint est une clé stable côté
            // navigateur pour UN abonnement donné.
            var existing = await db.PushSubscriptions.SingleOrDefaultAsync(s => s.Endpoint == request.Endpoint, ct);
            if (existing is not null)
                db.PushSubscriptions.Remove(existing);

            db.PushSubscriptions.Add(PushSubscriptionEntity.Create(
                user.Id, request.Endpoint, request.Keys.P256dh, request.Keys.Auth, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(ct);

            return Results.Ok();
        })
        .RequireAuthorization()
        .WithName("SubscribePush")
        .WithSummary("Enregistre (ou renouvelle) l'abonnement WebPush du navigateur courant.");

        group.MapPost("/unsubscribe", async (
            PushUnsubscribeRequestDto request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> users,
            NovAccesIdentityDbContext db,
            CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null)
                return Results.Unauthorized();

            // Scopé au propriétaire : un endpoint appartenant à un AUTRE
            // utilisateur ne peut pas être désabonné à sa place.
            var existing = await db.PushSubscriptions
                .SingleOrDefaultAsync(s => s.Endpoint == request.Endpoint && s.UserId == user.Id, ct);
            if (existing is not null)
            {
                db.PushSubscriptions.Remove(existing);
                await db.SaveChangesAsync(ct);
            }

            return Results.Ok();
        })
        .RequireAuthorization()
        .WithName("UnsubscribePush")
        .WithSummary("Retire l'abonnement WebPush du navigateur courant.");

        return group;
    }
}
