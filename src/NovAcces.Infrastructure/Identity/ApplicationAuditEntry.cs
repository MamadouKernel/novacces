namespace NovAcces.Infrastructure.Identity;

/// <summary>
/// Événement technique append-only couvrant chaque requête API, y compris les
/// appels d'authentification qui ne disposent pas encore d'un tenant résolu.
/// </summary>
public sealed class ApplicationAuditEntry
{
    public Guid Id { get; private set; }
    public string Actor { get; private set; } = default!;
    public string Method { get; private set; } = default!;
    public string Path { get; private set; } = default!;
    public int StatusCode { get; private set; }
    public string? SiteId { get; private set; }
    public string? IpAddress { get; private set; }
    public DateTimeOffset Timestamp { get; private set; }

    private ApplicationAuditEntry() { }

    public static ApplicationAuditEntry Create(
        string actor, string method, string path, int statusCode,
        string? siteId, string? ipAddress, DateTimeOffset timestamp) => new()
    {
        Id = Guid.NewGuid(),
        Actor = string.IsNullOrWhiteSpace(actor) ? "anonymous" : actor,
        Method = method,
        Path = path,
        StatusCode = statusCode,
        SiteId = siteId,
        IpAddress = ipAddress,
        Timestamp = timestamp,
    };
}