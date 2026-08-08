using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Notifications;

/// <summary>
/// Envoi via le service push d'Expo (https://exp.host) — pas de SDK serveur
/// nécessaire, une simple requête HTTP suffit. Best-effort : jamais de levée
/// d'exception, un terminal injoignable ne doit jamais interrompre la
/// supervision des autres.
/// </summary>
public sealed class ExpoPushSender : IExpoPushSender
{
    private const string ExpoPushUrl = "https://exp.host/--/api/v2/push/send";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ExpoPushSender> _logger;

    public ExpoPushSender(IHttpClientFactory httpFactory, ILogger<ExpoPushSender> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task SendAsync(string expoPushToken, string title, string body, CancellationToken ct)
    {
        try
        {
            using var client = _httpFactory.CreateClient();
            using var response = await client.PostAsJsonAsync(ExpoPushUrl, new
            {
                to = expoPushToken,
                title,
                body,
                sound = "default",
                priority = "high",
            }, ct);

            if (!response.IsSuccessStatusCode)
                _logger.LogWarning(
                    "Échec d'envoi Expo push (HTTP {Status}, best-effort, sans conséquence sur le scan).",
                    response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec d'envoi Expo push (best-effort, sans conséquence sur le scan).");
        }
    }
}
