namespace NovAcces.Infrastructure.Identity;

/// <summary>
/// Abonnement WebPush (PWA) d'un navigateur, rattaché au compte connecté au
/// moment de l'inscription. Sert à réveiller un onglet FERMÉ pour l'alerte de
/// dépassement (§7) — le canal SignalR existant ne couvre que l'onglet ouvert.
/// Un même utilisateur peut avoir plusieurs abonnements (plusieurs appareils/
/// navigateurs) ; un même Endpoint est unique (ré-abonnement = mise à jour).
/// </summary>
public sealed class PushSubscriptionEntity
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Endpoint { get; private set; } = default!;
    public string P256dh { get; private set; } = default!;
    public string Auth { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }

    private PushSubscriptionEntity() { }

    public static PushSubscriptionEntity Create(
        Guid userId, string endpoint, string p256dh, string auth, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Endpoint = endpoint,
        P256dh = p256dh,
        Auth = auth,
        CreatedAt = now,
    };
}
