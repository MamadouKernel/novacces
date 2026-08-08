using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure.Identity;
using NovAcces.Infrastructure.Persistence.Tenancy;
using NovAcces.Shared.Auth;

namespace NovAcces.Infrastructure.Notifications;

/// <summary>
/// Orchestre les notifications push d'un dépassement (§7) vers TROIS publics,
/// avec le MÊME niveau de détail que la diffusion SignalR équivalente
/// (ScanEventBroadcaster.BroadcastOverstayAsync) — moindre privilège identique :
///   - Sûreté du site + l'hôte concerné : nom du visiteur (leur propre visite/site).
///   - Admin/SuperAdmin (globaux, SiteId == null) : ANONYMISÉ, pas de nom.
///   - Terminaux agents du site (Expo) : nom du visiteur, même niveau que Sûreté.
/// </summary>
public sealed class OverstayPushNotifier : IOverstayPushNotifier
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly NovAccesIdentityDbContext _identityDb;
    private readonly IWebPushSender _webPush;
    private readonly IExpoPushSender _expoPush;
    private readonly ILogger<OverstayPushNotifier> _logger;

    public OverstayPushNotifier(
        UserManager<ApplicationUser> users,
        NovAccesIdentityDbContext identityDb,
        IWebPushSender webPush,
        IExpoPushSender expoPush,
        ILogger<OverstayPushNotifier> logger)
    {
        _users = users;
        _identityDb = identityDb;
        _webPush = webPush;
        _expoPush = expoPush;
        _logger = logger;
    }

    public async Task NotifyAsync(
        string siteId, string hostUserId, string visitorName,
        int overstayMinutes, int level, CancellationToken ct)
    {
        var title = level >= 3 ? "⚠ Dépassement critique" : "Dépassement de durée";
        var namedPayload = BuildWebPayload(title, $"{visitorName} — +{overstayMinutes} min sur site.", "/surete");
        var anonymizedPayload = BuildWebPayload(title, $"Site {siteId} — dépassement niveau {level}.", "/admin");

        try
        {
            var suretes = (await _users.GetUsersInRoleAsync(NovAccesRoles.Surete))
                .Where(u => string.Equals(u.SiteId, siteId, StringComparison.OrdinalIgnoreCase));

            var globalAdmins = (await _users.GetUsersInRoleAsync(NovAccesRoles.Admin))
                .Concat(await _users.GetUsersInRoleAsync(NovAccesRoles.SuperAdmin))
                .Where(u => u.SiteId is null);

            var namedRecipientIds = new HashSet<Guid>(suretes.Select(u => u.Id));
            if (Guid.TryParse(hostUserId, out var hostGuid))
                namedRecipientIds.Add(hostGuid);

            var anonymizedRecipientIds = globalAdmins.Select(u => u.Id).ToHashSet();
            // Un compte cumulant les deux (rare, ex. Admin ET Sûreté sur le même
            // site) ne doit recevoir que la version nominative, la plus précise.
            anonymizedRecipientIds.ExceptWith(namedRecipientIds);

            await SendToUsersAsync(namedRecipientIds, namedPayload, ct);
            await SendToUsersAsync(anonymizedRecipientIds, anonymizedPayload, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec de la notification WebPush de dépassement (best-effort).");
        }

        try
        {
            await SendToSiteTerminalsAsync(siteId, title, $"{visitorName} — +{overstayMinutes} min sur site.", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec de la notification Expo push de dépassement (best-effort).");
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

    private async Task SendToSiteTerminalsAsync(string siteId, string title, string body, CancellationToken ct)
    {
        // Terminaux actifs de CE site avec un jeton push enregistré (voir
        // Terminal.SetExpoPushToken, POST /api/agent/push-token).
        var terminals = await _identityDb.Terminals
            .Where(t => t.IsActive && t.ExpoPushToken != null && t.SiteIds.Contains(siteId))
            .Select(t => t.ExpoPushToken!)
            .ToListAsync(ct);

        foreach (var token in terminals)
            await _expoPush.SendAsync(token, title, body, ct);
    }

    private static string BuildWebPayload(string title, string body, string url) =>
        JsonSerializer.Serialize(new { title, body, url });
}
