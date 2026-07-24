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
        await _db.EnsureTenantResolvedAsync(ct);

        // FOR UPDATE : verrou pessimiste au niveau ligne. Garantit qu'en cas de
        // deux scans strictement simultanés du même QR (deux postes de contrôle),
        // le second attend la libération du verrou et voit l'état déjà consommé
        // par le premier — c'est la garantie réelle derrière REQ-SEC-03,
        // au-delà de la contrainte unique posée en ceinture de sécurité.
        //
        // La colonne est référencée entre guillemets ("VisitToken") : EF Core +
        // Npgsql conservent la casse PascalCase des noms de propriétés comme noms
        // de colonnes. Sans guillemets, PostgreSQL replierait l'identifiant en
        // minuscules (visit_token) — colonne inexistante, la requête échouerait.
        return await _db.Visits
            .FromSqlInterpolated($"SELECT * FROM visits WHERE \"VisitToken\" = {visitToken} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
    }

    public async Task<Visit?> GetByIdAsync(Guid visitId, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        return await _db.Visits.SingleOrDefaultAsync(v => v.Id == visitId, ct);
    }

    public async Task AddAsync(Visit visit, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        await _db.Visits.AddAsync(visit, ct);
    }

    public async Task<IReadOnlyCollection<Visit>> GetTodayActiveVisitsAsync(DateTimeOffset today, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        var startOfDay = new DateTimeOffset(today.Date, today.Offset);
        var endOfDay = startOfDay.AddDays(1);

        return await _db.Visits
            .Where(v => v.Status != Domain.Enums.VisitStatus.Revoked
                     && v.Status != Domain.Enums.VisitStatus.Expired
                     && (v.ScheduledAt == null || (v.ScheduledAt >= startOfDay && v.ScheduledAt < endOfDay)))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyCollection<Visit>> GetOnSiteAsync(CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        return await _db.Visits
            .Where(v => v.IsOnSite)
            .OrderBy(v => v.CheckedInAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyCollection<Visit>> GetByHostAsync(string hostUserId, int limit, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        return await _db.Visits
            .Where(v => v.HostUserId == hostUserId)
            .OrderByDescending(v => v.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyCollection<KnownVisitor>> GetKnownVisitorsAsync(int limit, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);

        // On récupère les visites récentes puis on garde, par nom, la plus
        // récente (regroupement en mémoire — volume modéré par site).
        var recent = await _db.Visits
            .OrderByDescending(v => v.CreatedAt)
            .Select(v => new { v.VisitorName, v.VisitorCompany, v.Motif, v.PlannedDurationMinutes })
            .Take(1000)
            .ToListAsync(ct);

        return recent
            .GroupBy(v => v.VisitorName)
            .Select(g => g.First())
            .OrderBy(v => v.VisitorName)
            .Take(limit)
            .Select(v => new KnownVisitor(v.VisitorName, v.VisitorCompany, v.Motif, v.PlannedDurationMinutes))
            .ToList();
    }

    public async Task<bool> HasActiveVisitForVisitorAsync(string visitorName, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        var name = visitorName.Trim();
        return await _db.Visits.AnyAsync(
            v => v.Status == Domain.Enums.VisitStatus.Valid && v.VisitorName.ToLower() == name.ToLower(), ct);
    }

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}

public sealed class ScanLogRepository : IScanLogRepository
{
    private readonly NovAccesDbContext _db;

    public ScanLogRepository(NovAccesDbContext db) => _db = db;

    public async Task AddAsync(ScanLogEntry entry, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        await _db.ScanLogs.AddAsync(entry, ct);
    }

    public async Task<IReadOnlyCollection<ScanLogEntry>> GetRecentAsync(int limit, string? query, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);

        IQueryable<ScanLogEntry> q = _db.ScanLogs;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLower();
            q = q.Where(e => e.VisitorName.ToLower().Contains(term)
                          || e.AgentId.ToLower().Contains(term)
                          || e.Detail.ToLower().Contains(term));
        }

        return await q.OrderByDescending(e => e.Timestamp).Take(limit).ToListAsync(ct);
    }

    public async Task<IReadOnlyCollection<ScanLogEntry>> GetSinceAsync(DateTimeOffset sinceUtc, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        return await _db.ScanLogs
            .Where(e => e.Timestamp >= sinceUtc)
            .OrderByDescending(e => e.Timestamp)
            .ToListAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
