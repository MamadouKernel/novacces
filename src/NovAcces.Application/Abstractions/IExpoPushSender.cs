namespace NovAcces.Application.Abstractions;

/// <summary>
/// Envoi d'une notification push à un terminal agent (app React Native/Expo)
/// via le service push d'Expo — même intention que <see cref="IWebPushSender"/>
/// côté navigateur : réveiller le terminal même app fermée (§7).
/// </summary>
public interface IExpoPushSender
{
    Task SendAsync(string expoPushToken, string title, string body, CancellationToken ct);
}
