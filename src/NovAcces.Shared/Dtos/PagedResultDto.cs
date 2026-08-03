namespace NovAcces.Shared.Dtos;

/// <summary>
/// Page d'une liste, avec assez d'information pour reconstruire une
/// pagination côté client (page courante, taille de page, total réel —
/// pas seulement le nombre d'éléments de CETTE page). Convention uniforme
/// pour tous les tableaux de l'app : requête via <c>?page=1&amp;pageSize=20</c>.
/// </summary>
public sealed record PagedResultDto<T>(
    IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
