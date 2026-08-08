namespace NovAcces.Application.Abstractions;

/// <summary>
/// Envoi d'une notification WebPush (PWA) à un abonnement de navigateur —
/// permet de réveiller un onglet FERMÉ (§7, alerte de dépassement), à la
/// différence du canal SignalR existant qui exige l'onglet ouvert.
/// </summary>
public interface IWebPushSender
{
    /// <summary>
    /// Envoie <paramref name="payloadJson"/> (déjà sérialisé) à l'abonnement.
    /// Renvoie false si le navigateur a définitivement révoqué l'abonnement
    /// (410/404) — l'appelant doit alors le supprimer, jamais le retenter.
    /// </summary>
    Task<bool> SendAsync(string endpoint, string p256dh, string auth, string payloadJson, CancellationToken ct);
}
