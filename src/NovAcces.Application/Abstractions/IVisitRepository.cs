using NovAcces.Domain.Entities;

namespace NovAcces.Application.Abstractions;

/// <summary>Visiteur connu et ses dernières valeurs (pré-remplissage).</summary>
public sealed record KnownVisitor(string Name, string Company, string Motif, int PlannedDurationMinutes);

public interface IVisitRepository
{
    /// <summary>
    /// Charge une visite pour mise à jour AVEC verrou pessimiste
    /// (SELECT ... FOR UPDATE) — condition indispensable à la garantie
    /// anti-rejeu en cas de scans strictement simultanés (REQ-SEC-03).
    /// </summary>
    Task<Visit?> GetForUpdateAsync(Guid visitToken, CancellationToken ct);

    Task<Visit?> GetByIdAsync(Guid visitId, CancellationToken ct);

    /// <summary>Lecture par jeton de visite, SANS verrou (confrontation de resync).</summary>
    Task<Visit?> GetByTokenAsync(Guid visitToken, CancellationToken ct);

    Task AddAsync(Visit visit, CancellationToken ct);

    /// <summary>Visites valides du jour, pour la génération de la liste hors ligne signée.</summary>
    Task<IReadOnlyCollection<Visit>> GetTodayActiveVisitsAsync(DateTimeOffset today, CancellationToken ct);

    /// <summary>Visiteurs actuellement sur site (entrés, pas encore sortis) — dashboard sûreté.</summary>
    Task<IReadOnlyCollection<Visit>> GetOnSiteAsync(CancellationToken ct);

    /// <summary>Demandes créées par un hôte (les plus récentes d'abord) — portail hôte.</summary>
    Task<IReadOnlyCollection<Visit>> GetByHostAsync(string hostUserId, int limit, CancellationToken ct);

    /// <summary>
    /// Visiteurs déjà connus du site, avec leurs dernières valeurs (entreprise,
    /// motif, durée) pour le pré-remplissage à l'autocomplétion (§8 maquette).
    /// </summary>
    Task<IReadOnlyCollection<KnownVisitor>> GetKnownVisitorsAsync(int limit, CancellationToken ct);

    /// <summary>
    /// Vrai s'il existe déjà une demande ACTIVE (statut Valid) pour ce visiteur —
    /// garde-fou anti-doublon à la création (une seule demande active par
    /// visiteur, cf. maquette du 22/07/2026). Portée sur nom + société : deux
    /// personnes homonymes de sociétés différentes ne doivent pas se bloquer
    /// mutuellement (comparaison insensible à la casse et aux espaces).
    /// </summary>
    Task<bool> HasActiveVisitForVisitorAsync(string visitorName, string visitorCompany, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>
/// Levée par l'implémentation de <see cref="IVisitRepository.SaveChangesAsync"/>
/// quand la contrainte unique "une seule demande active par visiteur" est
/// violée EN BASE (ceinture de sécurité derrière la vérification applicative
/// de <see cref="IVisitRepository.HasActiveVisitForVisitorAsync"/>, qui ne
/// protège pas seule contre deux créations strictement concurrentes du même
/// visiteur). L'Api la traduit en 409 Conflict, avec le même message que la
/// vérification amont — l'appelant ne voit aucune différence de comportement.
/// </summary>
public sealed class DuplicateActiveVisitException : Exception
{
    public DuplicateActiveVisitException()
        : base("Une demande active existe déjà pour ce visiteur.")
    {
    }
}

public interface IScanLogRepository
{
    Task AddAsync(ScanLogEntry entry, CancellationToken ct);

    /// <summary>
    /// Derniers scans journalisés (les plus récents d'abord), avec recherche
    /// optionnelle sur nom de visiteur / agent / détail — dashboard sûreté.
    /// </summary>
    Task<IReadOnlyCollection<ScanLogEntry>> GetRecentAsync(int limit, string? query, CancellationToken ct);

    /// <summary>
    /// Page de résultats de recherche dans le journal, avec le total réel —
    /// distinct de GetRecentAsync (aperçu tronqué, utilisé pour le flux temps
    /// réel initial et l'export CSV, pas pour l'écran de recherche paginé).
    /// </summary>
    Task<(IReadOnlyCollection<ScanLogEntry> Items, int TotalCount)> GetPagedAsync(
        int page, int pageSize, string? query, CancellationToken ct);

    /// <summary>Scans depuis un instant donné (synthèse quotidienne).</summary>
    Task<IReadOnlyCollection<ScanLogEntry>> GetSinceAsync(DateTimeOffset sinceUtc, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
