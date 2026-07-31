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
}