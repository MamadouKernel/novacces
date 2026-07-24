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
}
