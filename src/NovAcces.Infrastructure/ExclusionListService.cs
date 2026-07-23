using NovAcces.Application.Visits;

namespace NovAcces.Infrastructure;

/// <summary>
/// Implémentation minimale (jalon 1). En jalon 2 : table dédiée par tenant
/// (exclusion_entries) gérée depuis le dashboard sûreté, avec comparaison
/// normalisée (casse, accents) plutôt qu'une égalité stricte.
/// </summary>
public sealed class ExclusionListService : IExclusionListService
{
    public Task<bool> IsExcludedAsync(string visitorName, CancellationToken ct)
        => Task.FromResult(false);
}
