namespace NovAcces.Infrastructure.Identity;

/// <summary>
/// Paramètres du jeton JWT (section "Jwt" de la configuration). La clé de
/// signature est un secret : jamais versionnée (user-secrets / variable
/// d'environnement), au moins 32 octets pour HMAC-SHA256.
/// </summary>
public sealed class JwtOptions
{
    public string Issuer { get; set; } = "SigasAcces";
    public string Audience { get; set; } = "SigasAcces";
    public string SigningKey { get; set; } = default!;
    public int ExpiryMinutes { get; set; } = 60;

    /// <summary>Durée d'un poste d'agent (jeton de prise de poste), en heures.</summary>
    public int ShiftExpiryHours { get; set; } = 12;
    public int AgentExpiryHours { get; set; } = 8;
    public int RefreshExpiryHours { get; set; } = 24;
}
