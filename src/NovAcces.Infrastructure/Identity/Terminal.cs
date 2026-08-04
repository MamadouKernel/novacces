namespace NovAcces.Infrastructure.Identity;

/// <summary>
/// Terminal agent enrôlé. La clé API n'est jamais stockée en clair, seul son
/// hash SHA-256 est conservé. L'appareil est lié à l'installation via son
/// identifiant et sa clé publique enregistrée lors de l'activation QR.
/// </summary>
public sealed class Terminal
{
    public Guid Id { get; private set; }
    public string Label { get; private set; } = default!;
    public string ApiKeyHash { get; private set; } = default!;
    public List<string> SiteIds { get; private set; } = new();
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? DeviceInstanceId { get; private set; }
    public string? DevicePublicKeyPem { get; private set; }
    public DateTimeOffset? EnrolledAt { get; private set; }
    public bool IsEnrolled => !string.IsNullOrWhiteSpace(DeviceInstanceId) && EnrolledAt.HasValue;

    /// <summary>
    /// Suppression logique (archivage) : distincte de <see cref="IsActive"/>.
    /// Un terminal révoqué reste visible et son historique consultable ; un
    /// terminal supprimé disparaît des listes normales mais la ligne reste en
    /// base. Son DeviceInstanceId redevient disponible (un appareil physique
    /// peut être réenrôlé comme nouveau terminal) — voir l'index unique
    /// partiel dans NovAccesIdentityDbContext.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; private set; }
    public string? DeletedBy { get; private set; }

    private Terminal() { }

    public static Terminal Create(string label, string apiKeyHash, IReadOnlyList<string> siteIds, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Label = label,
        ApiKeyHash = apiKeyHash,
        SiteIds = siteIds.ToList(),
        IsActive = true,
        CreatedAt = now,
    };

    public void BindDevice(string deviceInstanceId, string devicePublicKeyPem, string apiKeyHash, DateTimeOffset now)
    {
        DeviceInstanceId = deviceInstanceId;
        DevicePublicKeyPem = devicePublicKeyPem;
        ApiKeyHash = apiKeyHash;
        EnrolledAt = now;
        IsActive = true;
        RevokedAt = null;
    }

    public void Revoke(DateTimeOffset now)
    {
        IsActive = false;
        RevokedAt = now;
    }

    /// <summary>
    /// Suppression logique : n'est permise que sur un terminal déjà révoqué
    /// (discipline en deux temps, comme pour un agent, un compte ou un site).
    /// </summary>
    public void Delete(DateTimeOffset now, string actor)
    {
        if (IsActive)
            throw new InvalidOperationException("Révoquez le terminal avant de le supprimer.");

        DeletedAt = now;
        DeletedBy = actor;
    }
}