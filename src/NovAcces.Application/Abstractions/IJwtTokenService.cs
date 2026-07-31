namespace NovAcces.Application.Abstractions;

/// <summary>
/// Émission des jetons d'accès JWT pour le portail web. Abstraction ici
/// (Application) ; implémentation cryptographique dans Infrastructure.
/// </summary>
public interface IJwtTokenService
{
    /// <param name="siteId">Site de rattachement, ou null pour un Admin global.</param>
    /// <returns>Le jeton signé et son instant d'expiration.</returns>
    (string Token, DateTimeOffset ExpiresAt) CreateToken(
        Guid userId, string email, string displayName, IEnumerable<string> roles, string? siteId);

    /// <summary>
    /// Jeton de « prise de poste » d'un agent, signé et à durée de vie courte
    /// (fin de service). Prouve que le matricule a été vérifié (matricule + PIN),
    /// pour tamponner les scans à cet agent sans qu'il puisse être auto-déclaré.
    /// </summary>
    (string Token, DateTimeOffset ExpiresAt) CreateShiftToken(string matricule, string displayName, string siteId);

    /// <summary>Valide un jeton de poste ; retourne l'identité de l'agent, ou null si invalide/expiré.</summary>
    /// <summary>JWT Agent du contrat mobile, utilisable sans clé de terminal.</summary>
    (string Token, DateTimeOffset ExpiresAt) CreateAgentToken(string matricule, string displayName, string siteId);

    ShiftIdentity? ValidateShiftToken(string token, string expectedSiteId);
}

/// <summary>Identité d'agent portée par un jeton de poste validé.</summary>
public sealed record ShiftIdentity(string Matricule, string DisplayName, string SiteId);
