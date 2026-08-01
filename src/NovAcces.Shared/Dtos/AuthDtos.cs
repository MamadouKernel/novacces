namespace NovAcces.Shared.Dtos;

/// <summary>Demande de connexion (portail web : Hôte / Sûreté / Admin).</summary>
public sealed record LoginRequestDto(string? Email, string? Password)
{
    public string? Matricule { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("motDePasse")]
    public string? MotDePasse { get; init; }

    public string EffectivePassword => string.IsNullOrWhiteSpace(MotDePasse) ? Password ?? string.Empty : MotDePasse;
}

/// <summary>
/// Réponse de connexion. AccessToken = JWT à présenter en Bearer sur les appels
/// suivants. ExpiresAt = expiration du jeton.
/// </summary>
public sealed record LoginResponseDto(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string DisplayName,
    IReadOnlyList<string> Roles,
    string? SiteId,
    string? RefreshToken = null,
    int ExpiresIn = 0);

public sealed record RefreshTokenRequestDto(string RefreshToken);
public sealed record AgentLoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn, AgentLoginIdentityDto Agent);
public sealed record AgentLoginIdentityDto(string Matricule, string Nom, string Role = "Agent");

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
/// Réponse de première connexion d'un compte privilégié sans 2FA. Aucun JWT
/// n'est délivré tant que l'enrôlement TOTP n'est pas terminé.
/// </summary>
public sealed record TwoFactorEnrollmentRequiredDto(
    bool RequiresTwoFactorEnrollment = true);

/// <summary>
/// Demande de bootstrap TOTP pour un compte privilégié qui ne possède encore
/// aucune session authentifiée. Le mot de passe est toujours vérifié côté API.
/// </summary>
public sealed record TwoFactorBootstrapRequestDto(string Email, string Password);
public sealed record TwoFactorBootstrapEnableRequestDto(string Email, string Password, string Code);

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

/// <summary>État actuel du 2FA pour l'utilisateur connecté (profil).</summary>
public sealed record TwoFactorStatusDto(bool Enabled);

/// <summary>
/// Second facteur au login. Code = TOTP à 6 chiffres OU code de récupération.
/// </summary>
public sealed record TwoFactorLoginRequestDto(string Email, string Password, string Code);

// ---- Administration ----

/// <summary>Demande de désactivation logique d'un compte par un administrateur.</summary>
public sealed record DeactivateUserRequestDto(string Reason);

/// <summary>Compte tel qu'affiché dans la console d'administration.</summary>
public sealed record AdminUserDto(
    Guid Id, string Email, string DisplayName, IReadOnlyList<string> Roles, string? SiteId, bool TwoFactorEnabled,
    bool IsDeactivated = false);

/// <summary>Provisionnement d'un site (schéma + modèle + journal append-only).</summary>
public sealed record ProvisionSiteRequestDto(string SiteId);

// ---- Profil : l'utilisateur modifie ses propres données ----

public sealed record UpdateDisplayNameRequestDto(string DisplayName);

public sealed record ChangePasswordRequestDto(string CurrentPassword, string NewPassword);

/// <summary>Ligne de la vue consolidée multi-sites (§10).</summary>
/// <summary>
/// Ligne de la vue consolidée multi-sites (§10). Au-delà de l'activité, elle
/// porte l'état des POSTES : combien de terminaux sont enrôlés sur le site,
/// combien ont donné signe de vie récemment, et combien de scans du jour ont
/// été validés en mode dégradé — c'est ce qui permet de voir d'un coup d'œil
/// qu'un site est en difficulté réseau.
/// </summary>
public sealed record AdminSiteOverviewDto(
    string SiteId,
    int OnSite,
    int ScansToday,
    int TerminalsEnrolled = 0,
    int TerminalsActive = 0,
    int DegradedScansToday = 0);

/// <summary>Sortie manuelle enregistrée depuis le dashboard sûreté (§7).</summary>
public sealed record ManualCheckOutResponseDto(
    Guid VisitId, string VisitorName, int PresenceMinutes, int OverstayMinutes);

// ---- Journal d'audit des actions d'administration/sûreté (§8.5) ----

/// <summary>Entrée du journal d'audit inaltérable, telle qu'affichée à la Sûreté/Admin.</summary>
/// <summary>Entrée technique globale tracée pour chaque requête API.</summary>
public sealed record ApplicationAuditDto(
    Guid Id, string Actor, string Method, string Path, int StatusCode,
    string? SiteId, string? IpAddress, DateTimeOffset Timestamp);

public sealed record AdminAuditDto(
    Guid Id, string Actor, string Action, string? TargetId, string Detail, DateTimeOffset Timestamp);

// ---- Rétention / purge des données (§7.3) ----

/// <summary>État de la politique de rétention (consultation Admin).</summary>
public sealed record RetentionStatusDto(
    bool Enabled, int VisitRetentionDays, int JournalRetentionDays, int RunIntervalHours);

/// <summary>Résultat d'une passe de rétention déclenchée manuellement, par site.</summary>
public sealed record RetentionRunResultDto(
    int TotalPurged, int TotalAnonymized, IReadOnlyList<SitePurgeDto> Sites,
    int ApplicationAuditPurged = 0, int RefreshSessionsPurged = 0);

public sealed record SitePurgeDto(string SiteId, int VisitsPurged, int ScanLogsAnonymized);
