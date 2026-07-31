using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Identity;

public sealed class ApplicationAuditLog : IApplicationAuditLog
{
    private readonly NovAccesIdentityDbContext _db;
    private readonly IDateTimeProvider _clock;

    public ApplicationAuditLog(NovAccesIdentityDbContext db, IDateTimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task RecordAsync(
        string actor, string method, string path, int statusCode,
        string? siteId, string? ipAddress, CancellationToken ct)
    {
        _db.ApplicationAudit.Add(ApplicationAuditEntry.Create(
            actor, method, path, statusCode, siteId, ipAddress, _clock.UtcNow));
        await _db.SaveChangesAsync(ct);
    }
}