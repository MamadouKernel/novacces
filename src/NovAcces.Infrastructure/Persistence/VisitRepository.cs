using Microsoft.EntityFrameworkCore;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Entities;

namespace NovAcces.Infrastructure.Persistence;

public sealed class VisitRepository : IVisitRepository
{
    private readonly NovAccesDbContext _db;

    public VisitRepository(NovAccesDbContext db) => _db = db;

    public async Task<Visit?> GetForUpdateAsync(Guid visitToken, CancellationToken ct)
    {
        await _db.EnsureTenantSchemaAppliedAsync(ct);

        // FOR UPDATE : verrou pessimiste au niveau ligne. Garantit qu'en cas de
        // deux scans strictement simultanés du même QR (deux postes de contrôle),
        // le second attend la libération du verrou et voit l'état déjà consommé
        // par le premier — c'est la garantie réelle derrière REQ-SEC-03,
        // au-delà de la contrainte unique posée en ceinture de sécurité.
        return await _db.Visits
            .FromSqlInterpolated($"SELECT * FROM visits WHERE visit_token = {visitToken} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
    }

    public async Task<Visit?> GetByIdAsync(Guid visitId, CancellationToken ct)
    {
        await _db.EnsureTenantSchemaAppliedAsync(ct);
        return await _db.Visits.SingleOrDefaultAsync(v => v.Id == visitId, ct);
    }

    public async Task AddAsync(Visit visit, CancellationToken ct)
    {
        await _db.EnsureTenantSchemaAppliedAsync(ct);
        await _db.Visits.AddAsync(visit, ct);
    }

    public async Task<IReadOnlyCollection<Visit>> GetTodayActiveVisitsAsync(DateTimeOffset today, CancellationToken ct)
    {
        await _db.EnsureTenantSchemaAppliedAsync(ct);
        var startOfDay = new DateTimeOffset(today.Date, today.Offset);
        var endOfDay = startOfDay.AddDays(1);

        return await _db.Visits
            .Where(v => v.Status != Domain.Enums.VisitStatus.Revoked
                     && v.Status != Domain.Enums.VisitStatus.Expired
                     && (v.ScheduledAt == null || (v.ScheduledAt >= startOfDay && v.ScheduledAt < endOfDay)))
            .ToListAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}

public sealed class ScanLogRepository : IScanLogRepository
{
    private readonly NovAccesDbContext _db;

    public ScanLogRepository(NovAccesDbContext db) => _db = db;

    public async Task AddAsync(ScanLogEntry entry, CancellationToken ct)
    {
        await _db.EnsureTenantSchemaAppliedAsync(ct);
        await _db.ScanLogs.AddAsync(entry, ct);
    }

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
