namespace NovAcces.Api.Auth;

/// <summary>
/// Règles de sécurité d'authentification configurables par environnement.
/// Les comptes Admin, SuperAdmin et Sûreté doivent activer TOTP avant de
/// recevoir un jeton de session lorsque RequireTwoFactorForPrivileged est vrai.
/// </summary>
public sealed class AuthenticationSecurityOptions
{
    public bool RequireTwoFactorForPrivileged { get; set; } = true;

    /// <summary>
    /// Active l'envoi réel du lien de réinitialisation de mot de passe
    /// (self-service). Reste à false tant que les identifiants SMTP réels ne
    /// sont pas renseignés — l'endpoint /forgot-password répond toujours le
    /// même message générique dans les deux cas (anti-énumération), seul
    /// l'envoi de l'email est concerné par ce drapeau.
    /// </summary>
    public bool PasswordResetEnabled { get; set; }
}