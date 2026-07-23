using NovAcces.Domain.Entities;

namespace NovAcces.Application.Abstractions;

public interface IVisitRepository
{
    /// <summary>
    /// Charge une visite pour mise à jour AVEC verrou pessimiste
    /// (SELECT ... FOR UPDATE) — condition indispensable à la garantie
    /// anti-rejeu en cas de scans strictement simultanés (REQ-SEC-03).
    /// </summary>
    Task<Visit?> GetForUpdateAsync(Guid visitToken, CancellationToken ct);

    Task<Visit?> GetByIdAsync(Guid visitId, CancellationToken ct);

    Task AddAsync(Visit visit, CancellationToken ct);

    /// <summary>Visites valides du jour, pour la génération de la liste hors ligne signée.</summary>
    Task<IReadOnlyCollection<Visit>> GetTodayActiveVisitsAsync(DateTimeOffset today, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}

public interface IScanLogRepository
{
    Task AddAsync(ScanLogEntry entry, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
