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

    /// <summary>
    /// Crée un agent (matricule unique par site). Le PIN est haché avant
    /// stockage. Lève <see cref="InvalidOperationException"/> si le matricule
    /// existe déjà sur ce site, ou s'il est déjà actif sur un AUTRE site
    /// (voir IAgentRegistry) : jamais actif à deux endroits.
    /// </summary>
    Task AddAsync(string matricule, string displayName, string pin, CancellationToken ct);

    /// <summary>Liste les agents du site (sans le PIN), pour l'administration.</summary>
    Task<IReadOnlyList<AgentIdentity>> ListAsync(CancellationToken ct);

    /// <summary>
    /// Désactive un agent sur ce site (départ, réaffectation vers un autre
    /// site) : son PIN ne permet plus de prendre de poste ici. Retourne false
    /// si le matricule est introuvable sur ce site.
    /// </summary>
    Task<bool> DeactivateAsync(string matricule, CancellationToken ct);

    /// <summary>
    /// Réactive un agent désactivé sur ce site (retour après réaffectation) :
    /// il retrouve son matricule d'origine, le PIN précédent restant valable.
    /// Retourne false si le matricule est introuvable sur ce site. Lève
    /// <see cref="InvalidOperationException"/> si ce matricule est déjà actif
    /// sur un AUTRE site (voir IAgentRegistry) : jamais actif à deux endroits.
    /// </summary>
    Task<bool> ReactivateAsync(string matricule, CancellationToken ct);

    /// <summary>
    /// Suppression logique (archivage) d'un agent déjà désactivé sur ce site.
    /// La ligne reste en base pour la traçabilité, mais disparaît de
    /// <see cref="ListAsync"/> et son matricule redevient disponible pour un
    /// nouvel agent. Retourne false si le matricule est introuvable (ou déjà
    /// supprimé) sur ce site. Lève <see cref="InvalidOperationException"/> si
    /// l'agent est encore actif (désactiver d'abord).
    /// </summary>
    Task<bool> DeleteAsync(string matricule, string actor, CancellationToken ct);

    /// <summary>Liste les agents supprimés (archivés) du site, en lecture seule, pour l'administration.</summary>
    Task<IReadOnlyList<ArchivedAgentIdentity>> ListArchivedAsync(CancellationToken ct);
}

/// <summary>Identité d'un agent, sans secret.</summary>
public sealed record AgentIdentity(string Matricule, string DisplayName, bool IsActive = true);

/// <summary>Identité d'un agent supprimé (archivé), pour la consultation en lecture seule.</summary>
public sealed record ArchivedAgentIdentity(
    string Matricule, string DisplayName, DateTimeOffset DeletedAt, string? DeletedBy);
