namespace NovAcces.Application.Abstractions;

/// <summary>
/// Annuaire des agents du site courant (par tenant). Gère la vérification du
/// couple matricule + PIN pour la prise de poste, et l'ajout d'un agent
/// (réservé à l'administration). Le PIN est haché ici (jamais stocké en clair).
/// </summary>
public interface IAgentDirectory
{
    /// <summary>Vérifie matricule + PIN. Retourne l'agent si valide et actif, sinon null.</summary>
    Task<AgentIdentity?> VerifyAsync(string matricule, string pin, CancellationToken ct);

    /// <summary>Crée un agent (matricule unique par site). Le PIN est haché avant stockage.</summary>
    Task AddAsync(string matricule, string displayName, string pin, CancellationToken ct);

    /// <summary>Liste les agents du site (sans le PIN), pour l'administration.</summary>
    Task<IReadOnlyList<AgentIdentity>> ListAsync(CancellationToken ct);

    /// <summary>
    /// Désactive un agent sur ce site (départ, réaffectation vers un autre
    /// site) : son PIN ne permet plus de prendre de poste ici. Retourne false
    /// si le matricule est introuvable sur ce site.
    /// </summary>
    Task<bool> DeactivateAsync(string matricule, CancellationToken ct);
}

/// <summary>Identité d'un agent, sans secret.</summary>
public sealed record AgentIdentity(string Matricule, string DisplayName, bool IsActive = true);
