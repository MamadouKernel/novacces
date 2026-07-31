namespace NovAcces.Infrastructure.Identity;

/// <summary>Session de rafraîchissement ; seul le hash du token est conservé.</summary>
public sealed class RefreshSession
{
    public Guid Id { get; private set; }
    public string SubjectType { get; private set; } = default!;
    public string SubjectId { get; private set; } = default!;
    public string? DisplayName { get; private set; }
    public string? SiteId { get; private set; }
    public string TokenHash { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    private RefreshSession() { }

    public static RefreshSession Create(string subjectType, string subjectId, string? displayName, string? siteId, string tokenHash, DateTimeOffset now, DateTimeOffset expiresAt) => new()
    {
        Id = Guid.NewGuid(), SubjectType = subjectType, SubjectId = subjectId,
        DisplayName = displayName, SiteId = siteId, TokenHash = tokenHash,
        CreatedAt = now, ExpiresAt = expiresAt,
    };

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
