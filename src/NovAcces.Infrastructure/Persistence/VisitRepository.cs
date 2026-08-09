using Microsoft.EntityFrameworkCore;
using Npgsql;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Entities;

namespace NovAcces.Infrastructure.Persistence;

public sealed class VisitRepository : IVisitRepository
{
    // Doit rester strictement identique à l'expression de l'index unique
    // partiel posé par la migration AddActiveVisitorUniqueIndex
    // (lower(btrim(...))) : une divergence romprait la garantie que la
    // vérification applicative et la contrainte base voient le même doublon.
    private const string ActiveVisitorIndexName = "IX_visits_ActiveVisitorKey";
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

    public async Task<Visit?> GetForUpdateByManualCodeHashAsync(IReadOnlyList<string> candidateHashes, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);

        // Même verrou FOR UPDATE que GetForUpdateAsync, même raisonnement
        // (REQ-SEC-03) — résolu par l'empreinte du code de secours, parmi
        // PLUSIEURS empreintes candidates (courante + legacy, voir
        // IVisitRepository). ANY(...) reste un index scan simple sur l'index
        // unique partiel de ManualCodeHash, pas un coût supplémentaire notable
        // pour 1-2 candidats.
        var candidates = candidateHashes.ToArray();
        return await _db.Visits
            .FromSqlInterpolated($"SELECT * FROM visits WHERE \"ManualCodeHash\" = ANY({candidates}) FOR UPDATE")
            .SingleOrDefaultAsync(ct);
    }

    public async Task<Visit?> GetForUpdateByIdAsync(Guid visitId, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);

        // Même verrou FOR UPDATE que GetForUpdateAsync (REQ-SEC-03) — résolu par
        // Id plutôt que VisitToken/ManualCodeHash (voir approbation sûreté).
        return await _db.Visits
            .FromSqlInterpolated($"SELECT * FROM visits WHERE \"Id\" = {visitId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
    }

    public async Task<Visit?> GetByIdAsync(Guid visitId, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        return await _db.Visits.SingleOrDefaultAsync(v => v.Id == visitId, ct);
    }

    public async Task<Visit?> GetByTokenAsync(Guid visitToken, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        return await _db.Visits.SingleOrDefaultAsync(v => v.VisitToken == visitToken, ct);
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

    public async Task<(IReadOnlyCollection<Visit> Items, int TotalCount)> GetPagedAsync(
        int page, int pageSize, string? query, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        var q = _db.Visits.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLower();
            q = q.Where(v => v.VisitorName.ToLower().Contains(term)
                           || v.VisitorCompany.ToLower().Contains(term)
                           || v.Motif.ToLower().Contains(term));
        }

        q = q.OrderByDescending(v => v.CreatedAt);
        var total = await q.CountAsync(ct);
        var items = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetHostUserIdsByVisitIdsAsync(
        IReadOnlyCollection<Guid> visitIds, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        if (visitIds.Count == 0)
            return new Dictionary<Guid, string>();

        return await _db.Visits
            .AsNoTracking()
            .Where(v => visitIds.Contains(v.Id))
            .Select(v => new { v.Id, v.HostUserId })
            .ToDictionaryAsync(v => v.Id, v => v.HostUserId, ct);
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

    public async Task<bool> HasActiveVisitForVisitorAsync(string visitorName, string visitorCompany, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        var name = visitorName.Trim().ToLower();
        var company = (visitorCompany ?? "").Trim().ToLower();
        return await _db.Visits.AnyAsync(
            v => v.Status == Domain.Enums.VisitStatus.Valid
              && v.VisitorName.ToLower() == name
              && v.VisitorCompany.ToLower() == company, ct);
    }

    public async Task ExpireStaleActiveVisitsAsync(string visitorName, string visitorCompany, DateTimeOffset now, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        var name = visitorName.Trim().ToLower();
        var company = (visitorCompany ?? "").Trim().ToLower();

        // Même clé que HasActiveVisitForVisitorAsync (nom + société,
        // normalisés) : seules les demandes qui bloqueraient une nouvelle
        // création sont candidates. IsOnSite exclut déjà les présents via
        // Visit.ExpireIfWindowPassed (une visite en cours n'est jamais expirée).
        var candidates = await _db.Visits
            .Where(v => v.Status == Domain.Enums.VisitStatus.Valid
                     && v.VisitorName.ToLower() == name
                     && v.VisitorCompany.ToLower() == company)
            .ToListAsync(ct);

        var changed = false;
        foreach (var visit in candidates)
            changed |= visit.ExpireIfWindowPassed(now);

        if (changed)
            await _db.SaveChangesAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsActiveVisitorUniqueViolation(ex))
        {
            // Deux créations strictement concurrentes du même visiteur (même
            // nom + société) ont toutes deux passé la vérification applicative
            // avant que l'une des deux n'écrive : la contrainte base tranche,
            // celle qui perd la course reçoit exactement l'erreur qu'elle
            // aurait eue si la vérification amont l'avait détectée en premier.
            throw new DuplicateActiveVisitException();
        }
    }

    private static bool IsActiveVisitorUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && pg.ConstraintName == ActiveVisitorIndexName;
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
        var q = ApplySearch(_db.ScanLogs, query);
        return await q.OrderByDescending(e => e.Timestamp).Take(limit).ToListAsync(ct);
    }

    public async Task<(IReadOnlyCollection<ScanLogEntry> Items, int TotalCount)> GetPagedAsync(
        int page, int pageSize, string? query, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        var q = ApplySearch(_db.ScanLogs.AsNoTracking(), query).OrderByDescending(e => e.Timestamp);

        var total = await q.CountAsync(ct);
        var items = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    // §9 : recherche par nom, ENTREPRISE, agent ou MOTIF. Entreprise et motif
    // ne sont volontairement pas recopiés dans le journal (minimisation :
    // celui-ci ne conserve que ce qui prouve un contrôle d'accès). On les
    // rejoint donc depuis « visits », ce qui donne le comportement démontré
    // sans dupliquer de données personnelles dans une table inaltérable.
    private IQueryable<ScanLogEntry> ApplySearch(IQueryable<ScanLogEntry> q, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return q;

        var term = query.Trim().ToLower();
        var matchingVisits = _db.Visits
            .Where(v => v.VisitorCompany.ToLower().Contains(term)
                     || v.Motif.ToLower().Contains(term))
            .Select(v => v.Id);

        return q.Where(e => e.VisitorName.ToLower().Contains(term)
                          || e.AgentId.ToLower().Contains(term)
                          || e.Detail.ToLower().Contains(term)
                          || matchingVisits.Contains(e.VisitId));
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

public sealed class ScanConfirmationRequestRepository : IScanConfirmationRequestRepository
{
    private readonly NovAccesDbContext _db;

    public ScanConfirmationRequestRepository(NovAccesDbContext db) => _db = db;

    public async Task AddAsync(ScanConfirmationRequest request, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        await _db.ScanConfirmationRequests.AddAsync(request, ct);
    }

    public async Task<ScanConfirmationRequest?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        return await _db.ScanConfirmationRequests.SingleOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<ScanConfirmationRequest?> GetPendingForVisitAsync(Guid visitId, Domain.Enums.CheckpointDirection direction, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        return await _db.ScanConfirmationRequests
            .Where(r => r.VisitId == visitId && r.Direction == direction
                     && r.Status == Domain.Enums.ConfirmationRequestStatus.Pending)
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyCollection<ScanConfirmationRequest>> GetPendingAsync(CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        return await _db.ScanConfirmationRequests
            .Where(r => r.Status == Domain.Enums.ConfirmationRequestStatus.Pending)
            .OrderByDescending(r => r.RequestedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyCollection<ScanConfirmationRequest>> ExpireStaleAsync(DateTimeOffset now, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);

        var candidates = await _db.ScanConfirmationRequests
            .Where(r => r.Status == Domain.Enums.ConfirmationRequestStatus.Pending && r.ExpiresAt <= now)
            .ToListAsync(ct);

        var justExpired = candidates.Where(r => r.ExpireIfPastDeadline(now)).ToList();
        if (justExpired.Count > 0)
            await _db.SaveChangesAsync(ct);

        return justExpired;
    }

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
