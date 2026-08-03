namespace NovAcces.Application.Abstractions;

/// <summary>
/// Énumère les sites (tenants) provisionnés. Utilisé par les traitements
/// transverses hors requête (ex. supervision des dépassements) qui doivent
/// balayer tous les sites. Ne dépend d'aucun tenant courant.
/// </summary>
public interface ISiteCatalog
{
    Task<IReadOnlyList<string>> GetSiteIdsAsync(CancellationToken ct);

    /// <summary>
    /// Le site est-il provisionné ? Appelé à la résolution de tenant, sur le
    /// chemin de chaque requête : l'implémentation doit être mise en cache.
    /// </summary>
    Task<bool> ExistsAsync(string siteId, CancellationToken ct);

    /// <summary>
    /// Le site est-il ACTIF (par opposition à provisionné mais désactivé,
    /// contrat non reconduit) ? Distinct de <see cref="ExistsAsync"/> : un site
    /// désactivé continue d'exister (schéma et données intacts), il cesse
    /// seulement de servir des requêtes (voir TenantResolutionMiddleware).
    /// Un site sans enregistrement de statut (provisionné avant l'existence de
    /// cette fonctionnalité, ou jamais désactivé) est considéré actif par
    /// défaut. Appelé sur le chemin de chaque requête : mis en cache.
    /// </summary>
    Task<bool> IsActiveAsync(string siteId, CancellationToken ct);

    /// <summary>
    /// Invalide les caches d'existence et de statut. À appeler après le
    /// provisionnement, la désactivation ou la réactivation d'un site, sinon
    /// l'état resterait périmé le temps du TTL.
    /// </summary>
    void Invalidate();
}
