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
    /// Invalide le cache d'existence. À appeler après le provisionnement d'un
    /// site, sinon celui-ci resterait introuvable le temps du TTL — un site
    /// fraîchement créé doit être utilisable immédiatement.
    /// </summary>
    void Invalidate();
}
