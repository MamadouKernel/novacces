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

    /// <summary>Email HTML soigné, aux couleurs de la marque — bouton plutôt qu'un lien nu.</summary>
    public static string Html(PasswordResetNotification n, NotificationBrandingOptions branding)
    {
        var name = EmailLayout.Enc(n.DisplayName);

        var body = $@"
          <p style=""margin:0 0 14px;font-size:17px;color:#10161d;"">Bonjour {name},</p>
          <p style=""margin:0 0 26px;font-size:15px;line-height:1.6;color:#3c4854;"">
            Une réinitialisation de votre mot de passe a été demandée pour ce compte.
            Cliquez sur le bouton ci-dessous pour en choisir un nouveau.
          </p>

          <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0""><tr><td align=""center"" style=""padding:6px 0 24px;"">
            {EmailLayout.Button(n.ResetLink, "Choisir un nouveau mot de passe")}
          </td></tr></table>

          <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#fbfaf6;border:1px solid #e4e1d6;border-radius:10px;"">
            <tr><td style=""padding:14px 16px;font-size:13px;line-height:1.6;color:#6b7784;"">
              Si vous n'êtes pas à l'origine de cette demande, ignorez cet email — votre mot
              de passe actuel reste valide et rien ne change.
            </td></tr>
          </table>";

        return EmailLayout.Wrap(
            "Réinitialisez votre mot de passe en un clic.",
            "Sécurité du compte", body, branding, branding.SupportContact);
    }
}
