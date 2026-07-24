namespace NovAcces.Application.Abstractions;

/// <summary>
/// Énumère les sites (tenants) provisionnés. Utilisé par les traitements
/// transverses hors requête (ex. supervision des dépassements) qui doivent
/// balayer tous les sites. Ne dépend d'aucun tenant courant.
/// </summary>
public interface ISiteCatalog
{
    Task<IReadOnlyList<string>> GetSiteIdsAsync(CancellationToken ct);
}
