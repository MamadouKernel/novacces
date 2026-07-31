namespace NovAcces.Api.Auth;

/// <summary>
/// Règles de sécurité d'authentification configurables par environnement.
/// Les comptes Admin, SuperAdmin et Sûreté doivent activer TOTP avant de
/// recevoir un jeton de session lorsque RequireTwoFactorForPrivileged est vrai.
/// </summary>
public sealed class AuthenticationSecurityOptions
{
    public bool RequireTwoFactorForPrivileged { get; set; } = true;
}