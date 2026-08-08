using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Enums;
using NovAcces.Infrastructure.Identity;
using NovAcces.Infrastructure.Persistence.Tenancy;
using NovAcces.Shared.Auth;

namespace NovAcces.Infrastructure.Notifications;

/// <summary>
/// Notifie la sûreté d'une demande de confirmation (push web + email — voir
/// IConfirmationNotifier) et le terminal demandeur de l'issue réelle. Même
/// squelette qu'OverstayPushNotifier (résolution des sûretés du site via
/// UserManager, envoi/nettoyage des abonnements push morts) — pas de canal
/// Admin/SuperAdmin ici : une demande de confirmation est une action
/// opérationnelle propre au site, contrairement à un dépassement qui
/// intéresse aussi la supervision globale.
/// </summary>
public sealed class ConfirmationNotifier : IConfirmationNotifier
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly NovAccesIdentityDbContext _identityDb;
    private readonly IWebPushSender _webPush;
    private readonly IExpoPushSender _expoPush;
    private readonly INotificationService _notifications;
    private readonly ITerminalDirectory _terminals;
    private readonly ILogger<ConfirmationNotifier> _logger;

    public ConfirmationNotifier(
        UserManager<ApplicationUser> users,
        NovAccesIdentityDbContext identityDb,
        IWebPushSender webPush,
        IExpoPushSender expoPush,
        INotificationService notifications,
        ITerminalDirectory terminals,
        ILogger<ConfirmationNotifier> logger)
    {
        _users = users;
        _identityDb = identityDb;
        _webPush = webPush;
        _expoPush = expoPush;
        _notifications = notifications;
        _terminals = terminals;
        _logger = logger;
    }

    public async Task NotifyRequestedAsync(
        string siteId, string visitorName, string? checkpointId, CheckpointDirection direction,
        DateTimeOffset requestedAt, CancellationToken ct)
    {
        var directionLabel = direction == CheckpointDirection.Entry ? "entrée" : "sortie";

        List<ApplicationUser> suretes;
        try
        {
            suretes = (await _users.GetUsersInRoleAsync(NovAccesRoles.Surete))
                .Where(u => string.Equals(u.SiteId, siteId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec de la résolution des comptes Sûreté pour la notification de demande.");
            return;
        }

        if (suretes.Count == 0) return;

        try
        {
            var payload = BuildWebPayload(
                "Confirmation requise", $"{visitorName} — {directionLabel} sans QR/code.", "/surete");
            await SendToUsersAsync(suretes.Select(u => u.Id).ToHashSet(), payload, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec de la notification WebPush de demande de confirmation (best-effort).");
        }

        foreach (var surete in suretes)
        {
            if (string.IsNullOrWhiteSpace(surete.Email)) continue;
            try
            {
                await _notifications.NotifySureteConfirmationRequestAsync(new SureteConfirmationRequestNotification(
                    surete.Email!, visitorName, checkpointId, directionLabel, requestedAt), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Échec de l'email de demande de confirmation à un compte Sûreté (best-effort).");
            }
        }
    }

    public async Task NotifyResolvedAsync(Guid requestingTerminalId, bool granted, string visitorName, CancellationToken ct)
    {
        try
        {
            var token = await _terminals.GetExpoPushTokenAsync(requestingTerminalId, ct);
            if (string.IsNullOrWhiteSpace(token)) return;

            var title = granted ? "Accès confirmé" : "Accès refusé";
            var body = granted
                ? $"{visitorName} — la sûreté a confirmé l'accès."
                : $"{visitorName} — accès refusé (voir le poste de garde).";
            await _expoPush.SendAsync(token, title, body, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec de la notification push de résolution de confirmation (best-effort).");
        }
    }

    private async Task SendToUsersAsync(IReadOnlySet<Guid> userIds, string payloadJson, CancellationToken ct)
    {
        if (userIds.Count == 0) return;

        var subscriptions = await _identityDb.PushSubscriptions
            .Where(s => userIds.Contains(s.UserId))
            .ToListAsync(ct);

        var dead = new List<PushSubscriptionEntity>();
        foreach (var sub in subscriptions)
        {
            var ok = await _webPush.SendAsync(sub.Endpoint, sub.P256dh, sub.Auth, payloadJson, ct);
            if (!ok) dead.Add(sub);
        }

        if (dead.Count > 0)
        {
            _identityDb.PushSubscriptions.RemoveRange(dead);
            await _identityDb.SaveChangesAsync(ct);
        }
    }

    private static string BuildWebPayload(string title, string body, string url) =>
        JsonSerializer.Serialize(new { title, body, url });
}
