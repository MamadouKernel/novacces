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

    /// <summary>Visiteurs actuellement sur site (entrés, pas encore sortis) — dashboard sûreté.</summary>
    Task<IReadOnlyCollection<Visit>> GetOnSiteAsync(CancellationToken ct);

    /// <summary>Demandes créées par un hôte (les plus récentes d'abord) — portail hôte.</summary>
    Task<IReadOnlyCollection<Visit>> GetByHostAsync(string hostUserId, int limit, CancellationToken ct);

    /// <summary>Noms de visiteurs déjà connus du site (autocomplétion, REQ maquette).</summary>
    Task<IReadOnlyCollection<string>> GetKnownVisitorNamesAsync(int limit, CancellationToken ct);

    /// <summary>
    /// Vrai s'il existe déjà une demande ACTIVE (statut Valid) pour ce visiteur —
    /// garde-fou anti-doublon à la création (une seule demande active par visiteur,
    /// cf. maquette du 22/07/2026).
    /// </summary>
    Task<bool> HasActiveVisitForVisitorAsync(string visitorName, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}

public interface IScanLogRepository
{
    Task AddAsync(ScanLogEntry entry, CancellationToken ct);

    /// <summary>
    /// Derniers scans journalisés (les plus récents d'abord), avec recherche
    /// optionnelle sur nom de visiteur / agent / détail — dashboard sûreté.
    /// </summary>
    Task<IReadOnlyCollection<ScanLogEntry>> GetRecentAsync(int limit, string? query, CancellationToken ct);

    /// <summary>Scans depuis un instant donné (synthèse quotidienne).</summary>
    Task<IReadOnlyCollection<ScanLogEntry>> GetSinceAsync(DateTimeOffset sinceUtc, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
