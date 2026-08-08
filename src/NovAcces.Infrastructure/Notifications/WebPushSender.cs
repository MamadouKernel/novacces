using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;
using WebPush;

namespace NovAcces.Infrastructure.Notifications;

public sealed class WebPushOptions
{
    public string VapidPublicKey { get; set; } = "";
    public string VapidPrivateKey { get; set; } = "";
    public string Subject { get; set; } = "";
}

public sealed class WebPushSender : IWebPushSender
{
    private readonly WebPushClient _client = new();
    private readonly VapidDetails _vapid;
    private readonly ILogger<WebPushSender> _logger;

    public WebPushSender(IOptions<WebPushOptions> options, ILogger<WebPushSender> logger)
    {
        var opts = options.Value;
        _vapid = new VapidDetails(opts.Subject, opts.VapidPublicKey, opts.VapidPrivateKey);
        _logger = logger;
    }

    public async Task<bool> SendAsync(string endpoint, string p256dh, string auth, string payloadJson, CancellationToken ct)
    {
        var subscription = new PushSubscription(endpoint, p256dh, auth);
        try
        {
            await _client.SendNotificationAsync(subscription, payloadJson, _vapid, ct);
            return true;
        }
        catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // Abonnement révoqué par le navigateur (désinstallation, permission
            // retirée...) : signale à l'appelant de le supprimer, pas une panne
            // à journaliser bruyamment.
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec d'envoi WebPush (best-effort, sans conséquence sur le scan).");
            return true; // pas 410/404 : abonnement peut-être valide, ne PAS le supprimer sur un aléa réseau
        }
    }
}
