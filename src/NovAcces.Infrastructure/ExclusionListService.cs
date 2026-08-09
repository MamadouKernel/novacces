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

    public async Task<bool> IsExcludedAsync(string visitorName, string? visitorEmail, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        var normalizedName = ExclusionEntry.Normalize(visitorName);
        var normalizedEmail = ExclusionEntry.NormalizeEmail(visitorEmail);

        // Filtré par nom au niveau base (indexé, peu de candidats en pratique),
        // puis la précision par email — s'il y en a une sur l'entrée — est
        // appliquée en mémoire sur ce petit lot (voir ExclusionMatchKey.Matches).
        var candidateEmails = await _db.ExclusionEntries
            .Where(e => e.NormalizedName == normalizedName)
            .Select(e => e.NormalizedEmail)
            .ToListAsync(ct);

        return candidateEmails.Any(entryEmail => entryEmail is null || entryEmail == normalizedEmail);
    }

    public async Task<IReadOnlyCollection<ExclusionMatchKey>> GetMatchKeysAsync(CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        return await _db.ExclusionEntries
            .Select(e => new ExclusionMatchKey(e.NormalizedName, e.NormalizedEmail))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ExclusionEntryView>> ListAsync(CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        return await _db.ExclusionEntries
            .OrderBy(e => e.DisplayName)
            .Select(e => new ExclusionEntryView(e.Id, e.DisplayName, e.Reason, e.AddedBy, e.CreatedAt, e.Email))
            .ToListAsync(ct);
    }

    public async Task<Guid> AddAsync(string displayName, string reason, string addedBy, string? email, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);

        var normalizedName = ExclusionEntry.Normalize(displayName);
        var normalizedEmail = ExclusionEntry.NormalizeEmail(email);

        // Idempotent sur (nom, email) — pas sur le nom seul : une entrée large
        // et une entrée précisée par email pour le même nom sont deux entrées
        // distinctes et légitimes (voir IExclusionListService.AddAsync).
        var existing = await _db.ExclusionEntries
            .Where(e => e.NormalizedName == normalizedName && e.NormalizedEmail == normalizedEmail)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is { } id)
            return id; // idempotent : déjà exclu

        var entry = ExclusionEntry.Create(displayName, reason, addedBy, DateTimeOffset.UtcNow, email);
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
