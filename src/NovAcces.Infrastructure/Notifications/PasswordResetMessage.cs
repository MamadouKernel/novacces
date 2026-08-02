using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Notifications;

/// <summary>
/// Rédaction du message de réinitialisation de mot de passe (self-service).
/// Le lien contient le jeton Identity, à usage unique et à durée limitée
/// (politique par défaut ASP.NET Core Identity) — aucune donnée personnelle
/// sensible n'est incluse dans le corps au-delà du nom affiché du compte.
/// </summary>
internal static class PasswordResetMessage
{
    public static string Subject(NotificationBrandingOptions branding) =>
        $"[{branding.ProductName}] Réinitialisation de votre mot de passe";

    public static string PlainText(PasswordResetNotification n, NotificationBrandingOptions branding)
    {
        var signature = string.IsNullOrWhiteSpace(branding.SupportContact)
            ? $"\n\n--\n{branding.ProductName} — {branding.OrganizationName}"
            : $"\n\n--\n{branding.ProductName} — {branding.OrganizationName}\n{branding.SupportContact}";

        return $"Bonjour {n.DisplayName},\n\n"
            + "Une réinitialisation de votre mot de passe a été demandée pour ce compte.\n\n"
            + $"Pour choisir un nouveau mot de passe, ouvrez ce lien :\n{n.ResetLink}\n\n"
            + "Si vous n'êtes pas à l'origine de cette demande, ignorez cet email — "
            + "votre mot de passe actuel reste valide et rien ne change."
            + signature;
    }
}
