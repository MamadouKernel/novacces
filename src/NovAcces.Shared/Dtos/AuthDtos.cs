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

// ---- 2FA TOTP (Jalon 2, incrément 2) ----

/// <summary>
/// Réponse d'un login lorsqu'un second facteur est requis : aucun jeton n'est
/// délivré à ce stade. Le client doit rappeler /api/auth/login/2fa avec le code.
/// </summary>
public sealed record TwoFactorRequiredDto(bool RequiresTwoFactor = true);

/// <summary>
/// Données d'enrôlement TOTP : clé partagée (saisie manuelle possible dans
/// l'app d'authentification) et URI otpauth:// (à encoder en QR côté client).
/// </summary>
public sealed record TwoFactorSetupDto(string SharedKey, string AuthenticatorUri);

/// <summary>Activation du 2FA : code TOTP courant à valider.</summary>
public sealed record EnableTwoFactorRequestDto(string Code);

/// <summary>Codes de récupération remis une seule fois à l'activation du 2FA.</summary>
public sealed record TwoFactorRecoveryCodesDto(IReadOnlyList<string> RecoveryCodes);

/// <summary>Désactivation du 2FA : mot de passe exigé pour ré-authentifier l'action.</summary>
public sealed record DisableTwoFactorRequestDto(string Password);

/// <summary>
/// Second facteur au login. Code = TOTP à 6 chiffres OU code de récupération.
/// </summary>
public sealed record TwoFactorLoginRequestDto(string Email, string Password, string Code);

// ---- Administration ----

/// <summary>Compte tel qu'affiché dans la console d'administration.</summary>
public sealed record AdminUserDto(
    Guid Id, string Email, string DisplayName, IReadOnlyList<string> Roles, string? SiteId, bool TwoFactorEnabled);

/// <summary>Provisionnement d'un site (schéma + modèle + journal append-only).</summary>
public sealed record ProvisionSiteRequestDto(string SiteId);

// ---- Profil : l'utilisateur modifie ses propres données ----

public sealed record UpdateDisplayNameRequestDto(string DisplayName);

public sealed record ChangePasswordRequestDto(string CurrentPassword, string NewPassword);

/// <summary>Ligne de la vue consolidée multi-sites (§10).</summary>
public sealed record AdminSiteOverviewDto(string SiteId, int OnSite, int ScansToday);

// ---- Journal d'audit des actions d'administration/sûreté (§8.5) ----

/// <summary>Entrée du journal d'audit inaltérable, telle qu'affichée à la Sûreté/Admin.</summary>
public sealed record AdminAuditDto(
    Guid Id, string Actor, string Action, string? TargetId, string Detail, DateTimeOffset Timestamp);

// ---- Rétention / purge des données (§7.3) ----

/// <summary>État de la politique de rétention (consultation Admin).</summary>
public sealed record RetentionStatusDto(bool Enabled, int VisitRetentionDays, int RunIntervalHours);

/// <summary>Résultat d'une purge déclenchée manuellement, par site.</summary>
public sealed record RetentionRunResultDto(int TotalPurged, IReadOnlyList<SitePurgeDto> Sites);

public sealed record SitePurgeDto(string SiteId, int VisitsPurged);
