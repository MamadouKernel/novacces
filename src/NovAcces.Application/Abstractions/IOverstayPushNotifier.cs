namespace NovAcces.Application.Abstractions;

/// <summary>
/// Notifications push (WebPush navigateur + Expo mobile) déclenchées par un
/// dépassement de durée (§7) — complète la diffusion SignalR existante
/// (IScanEventBroadcaster.BroadcastOverstayAsync), qui n'atteint que les
/// clients déjà OUVERTS. Best-effort partout : une panne d'envoi ne doit
/// jamais interrompre la supervision des autres visiteurs.
/// </summary>
public interface IOverstayPushNotifier
{
    Task NotifyAsync(
        string siteId, string hostUserId, string visitorName,
        int overstayMinutes, int level, CancellationToken ct);
}
