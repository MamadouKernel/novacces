namespace NovAcces.Infrastructure.Notifications;

/// <summary>
/// Habillage commun (en-tête, pied de page, bouton) partagé par tous les
/// emails de l'application — pour qu'ils se lisent comme UN seul système
/// visuel (aux couleurs de la marque) plutôt que des templates indépendants
/// qui dérivent les uns des autres. Tableaux + styles inline uniquement :
/// pas de CSS externe ni de flexbox/grid, non fiables dans les clients mail.
/// </summary>
internal static class EmailLayout
{
    /// <summary>
    /// Enveloppe complète (HTML valide) : en-tête de marque, bandeau amber,
    /// libellé "eyebrow", contenu propre au message, pied de page (support +
    /// mention automatique). <paramref name="bodyHtml"/> est déjà du HTML —
    /// à l'appelant de faire passer du texte utilisateur par <see cref="Enc"/>.
    /// </summary>
    public static string Wrap(
        string preheader, string eyebrow, string bodyHtml,
        NotificationBrandingOptions branding, string? supportContact = null)
    {
        var year = DateTimeOffset.UtcNow.Year;
        var support = string.IsNullOrWhiteSpace(supportContact)
            ? ""
            : $"<p style=\"margin:0 0 6px;color:#6b7784;font-size:13px;\">Une question ? " +
              $"<a href=\"mailto:{Enc(supportContact!)}\" style=\"color:#0e2a3a;text-decoration:underline;\">{Enc(supportContact!)}</a></p>";

        return $@"<!DOCTYPE html>
<html lang=""fr""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""><meta name=""color-scheme"" content=""light""></head>
<body style=""margin:0;background:#eef0ec;font-family:'Segoe UI',Roboto,Arial,sans-serif;color:#10161d;"">
  <!-- Texte d'aperçu (masqué, améliore la ligne affichée dans la boîte de réception) -->
  <div style=""display:none;max-height:0;overflow:hidden;font-size:1px;line-height:1px;color:#eef0ec;"">{Enc(preheader)}</div>

  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#eef0ec;padding:28px 12px;"">
    <tr><td align=""center"">
      <table role=""presentation"" width=""560"" cellpadding=""0"" cellspacing=""0"" style=""max-width:560px;width:100%;background:#ffffff;border-radius:12px;overflow:hidden;box-shadow:0 2px 10px rgba(14,42,58,.08);"">

        <!-- En-tête -->
        <tr><td style=""background:#0e2a3a;padding:22px 28px;"">
          <span style=""font-size:21px;font-weight:700;letter-spacing:.5px;color:#ffffff;"">SIGAS<span style=""color:#f5a300;"">ACCÈS</span></span>
          <span style=""display:block;margin-top:3px;font-size:12px;letter-spacing:.4px;color:rgba(255,255,255,.65);text-transform:uppercase;"">Contrôle des accès visiteurs</span>
        </td></tr>
        <tr><td style=""height:4px;line-height:4px;font-size:0;background:#f5a300;"">&nbsp;</td></tr>

        <!-- Corps -->
        <tr><td style=""padding:32px 28px 8px;"">
          <p style=""margin:0 0 16px;font-size:11px;font-weight:700;letter-spacing:1.2px;color:#f5a300;text-transform:uppercase;"">{Enc(eyebrow)}</p>
          {bodyHtml}
        </td></tr>

        <!-- Pied -->
        <tr><td style=""padding:18px 28px 22px;border-top:1px solid #eceae0;background:#fbfaf6;"">
          {support}
          <p style=""margin:0 0 4px;color:#9aa7b2;font-size:12px;"">Message automatique envoyé via {Enc(branding.ProductName)}. Merci de ne pas répondre à cet email.</p>
          <p style=""margin:0;color:#b7bdb0;font-size:11px;"">© {year} {Enc(branding.OrganizationName)}</p>
        </td></tr>
      </table>
    </td></tr>
  </table>
</body></html>";
    }

    /// <summary>
    /// Bouton d'action (ex. réinitialisation de mot de passe). Un simple lien
    /// stylé en bouton — pas de VML pour Outlook desktop (complexité non
    /// justifiée ici) : le rendu reste un rectangle cliquable correct partout,
    /// juste avec des angles carrés plutôt qu'arrondis sur Outlook desktop.
    /// </summary>
    public static string Button(string href, string label) => $@"
          <table role=""presentation"" cellpadding=""0"" cellspacing=""0"" style=""margin:4px 0 8px;""><tr>
            <td style=""background:#f5a300;border-radius:8px;"">
              <a href=""{Enc(href)}"" target=""_blank"" rel=""noopener"" style=""display:inline-block;padding:14px 34px;font-size:15px;font-weight:700;color:#0e2a3a;text-decoration:none;border-radius:8px;"">{Enc(label)}</a>
            </td>
          </tr></table>";

    /// <summary>Petit badge coloré (ex. statut d'un événement visiteur).</summary>
    public static string Pill(string text, string background, string foreground) =>
        $@"<span style=""display:inline-block;padding:5px 14px;background:{background};color:{foreground};font-size:11px;font-weight:700;letter-spacing:.8px;text-transform:uppercase;border-radius:999px;"">{Enc(text)}</span>";

    public static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);
}
