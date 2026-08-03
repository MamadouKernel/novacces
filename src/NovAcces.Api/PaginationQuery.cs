namespace NovAcces.Api;

/// <summary>
/// Normalisation des paramètres de pagination (<c>?page=</c>, <c>?pageSize=</c>),
/// partagée par tous les endpoints listant un tableau (convention uniforme,
/// voir PagedResultDto). Page 1-indexée ; toute valeur absente ou invalide
/// retombe sur des valeurs sûres plutôt que de renvoyer une erreur — la
/// pagination est un confort d'affichage, pas une validation stricte.
/// </summary>
public static class PaginationQuery
{
    public static (int Page, int PageSize) Normalize(
        int? page, int? pageSize, int defaultPageSize = 20, int maxPageSize = 100)
    {
        var p = page is > 0 ? page.Value : 1;
        var size = pageSize is > 0 ? Math.Min(pageSize.Value, maxPageSize) : defaultPageSize;
        return (p, size);
    }
}
