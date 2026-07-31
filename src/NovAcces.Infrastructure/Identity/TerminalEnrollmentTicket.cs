namespace NovAcces.Infrastructure.Identity;

/// <summary>
/// Invitation temporaire d'un terminal. Le token en clair n'est jamais stocké;
/// seul TokenHash est persistant.
/// </summary>
public sealed class TerminalEnrollmentTicketEntity
{
    public Guid Id { get; private set; }
    public Guid TerminalId { get; private set; }
    public string TokenHash { get; private set; } = default!;
    public string CreatedBy { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? DeviceInstanceId { get; private set; }

    private TerminalEnrollmentTicketEntity() { }

    public static TerminalEnrollmentTicketEntity Create(
        Guid terminalId, string tokenHash, string createdBy,
        DateTimeOffset createdAt, DateTimeOffset expiresAt) => new()
    {
        Id = Guid.NewGuid(),
        TerminalId = terminalId,
        TokenHash = tokenHash,
        CreatedBy = createdBy,
        CreatedAt = createdAt,
        ExpiresAt = expiresAt,
    };

    public bool IsUsable(DateTimeOffset now) =>
        UsedAt is null && RevokedAt is null && now < ExpiresAt;

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    public void Consume(DateTimeOffset now, string deviceInstanceId)
    {
        UsedAt = now;
        DeviceInstanceId = deviceInstanceId;
    }
}