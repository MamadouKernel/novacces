using Microsoft.EntityFrameworkCore;
using NovAcces.Application.Visits;
using NovAcces.Domain.Entities;
using NovAcces.Infrastructure.Persistence;

namespace NovAcces.Infrastructure;

/// <summary>
/// Liste d'exclusion par site (REQ-F-11), stockée dans le schéma du tenant.
/// La comparaison se fait sur le nom NORMALISÉ (casse et accents neutralisés,
/// voir ExclusionEntry.Normalize) pour éviter les contournements triviaux.
/// </summary>
public sealed class ExclusionListService : IExclusionListService
{
    private readonly NovAccesDbContext _db;

    public ExclusionListService(NovAccesDbContext db) => _db = db;

    public async Task<bool> IsExcludedAsync(string visitorName, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        var normalized = ExclusionEntry.Normalize(visitorName);
        return await _db.ExclusionEntries.AnyAsync(e => e.NormalizedName == normalized, ct);
    }

    public async Task<IReadOnlySet<string>> GetExcludedNormalizedNamesAsync(CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        var names = await _db.ExclusionEntries
            .Select(e => e.NormalizedName)
            .ToListAsync(ct);
        return names.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<ExclusionEntryView>> ListAsync(CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        return await _db.ExclusionEntries
            .OrderBy(e => e.DisplayName)
            .Select(e => new ExclusionEntryView(e.Id, e.DisplayName, e.Reason, e.AddedBy, e.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<Guid> AddAsync(string displayName, string reason, string addedBy, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);

        var normalized = ExclusionEntry.Normalize(displayName);
        var existing = await _db.ExclusionEntries
            .Where(e => e.NormalizedName == normalized)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is { } id)
            return id; // idempotent : déjà exclu

        var entry = ExclusionEntry.Create(displayName, reason, addedBy, DateTimeOffset.UtcNow);
        _db.ExclusionEntries.Add(entry);
        await _db.SaveChangesAsync(ct);
        return entry.Id;
    }

    public async Task<ExclusionEntryView?> RemoveAsync(Guid id, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        var entry = await _db.ExclusionEntries.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null)
            return null;

        // On capture le contenu AVANT suppression : c'est ce que l'appelant
        // inscrira au journal d'audit inaltérable.
        var removed = new ExclusionEntryView(
            entry.Id, entry.DisplayName, entry.Reason, entry.AddedBy, entry.CreatedAt);

        _db.ExclusionEntries.Remove(entry);
        await _db.SaveChangesAsync(ct);
        return removed;
    }
}
