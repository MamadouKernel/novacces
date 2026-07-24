namespace NovAcces.Shared.Dtos;

/// <summary>Demande de connexion (portail web : Hôte / Sûreté / Admin).</summary>
public sealed record LoginRequestDto(string Email, string Password);

/// <summary>
/// Réponse de connexion. AccessToken = JWT à présenter en Bearer sur les appels
/// suivants. ExpiresAt = expiration du jeton.
/// </summary>
public sealed record LoginResponseDto(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string DisplayName,
    IReadOnlyList<string> Roles,
    string? SiteId);

/// <summary>
/// Création d'un compte (réservée à l'Admin). SiteId null = compte global
/// (autre Admin) ; sinon compte rattaché à un site précis.
/// </summary>
public sealed record RegisterUserRequestDto(
    string Email,
    string Password,
    string DisplayName,
    string Role,
    string? SiteId);
