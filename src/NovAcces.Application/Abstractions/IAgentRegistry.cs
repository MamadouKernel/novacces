namespace NovAcces.Application.Abstractions;

/// <summary>
/// Registre global (transverse aux sites) du matricule d'agent ACTIF, garanti
/// par une contrainte d'unicité en base — pas par une vérification
/// applicative racée. Empêche qu'un même matricule soit actif sur deux sites
/// en même temps (voir AgentDirectory, qui appelle ce registre à chaque
/// création/désactivation/réactivation).
/// </summary>
public interface IAgentRegistry
{
    /// <summary>
    /// Réclame ce matricule pour ce site, de façon atomique. Renvoie le site
    /// qui le détient déjà si la réclamation échoue (conflit), sinon null
    /// (réclamation réussie).
    /// </summary>
    Task<string?> TryClaimAsync(string matricule, string siteId, CancellationToken ct);

    /// <summary>Libère la réclamation, uniquement si elle appartient à ce site (no-op sinon).</summary>
    Task ReleaseAsync(string matricule, string siteId, CancellationToken ct);
}
