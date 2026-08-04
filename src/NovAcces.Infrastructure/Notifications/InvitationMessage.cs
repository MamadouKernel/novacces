using System.Net;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Notifications;

/// <summary>
/// Rédige le message d'invitation (email) dans un ton professionnel, poli
/// et courtois. Copie centralisée ici pour rester cohérente et facile à
/// faire évoluer.
///
/// Abidjan/San Pedro sont en UTC+0 (pas d'heure d'été) : les horodatages
/// UTC des visites correspondent à l'heure locale, aucune conversion requise.
/// </summary>
internal static class InvitationMessage
{
    public static string Subject(VisitInvitationNotification n) =>
        $"Votre QR Code d'accès visiteur — présentez-le au poste de contrôle";

    /// <summary>Version texte (base commune de l'email, sans HTML).</summary>
    public static string PlainText(VisitInvitationNotification n, NotificationBrandingOptions b)
    {
        var lines = new List<string>
        {
            $"Bonjour {n.VisitorName},",
            "",
            IntroSentence(n),
            "",
            "Vous trouverez votre QR Code d'accès personnel en pièce jointe (et ci-dessous s'il s'affiche). Présentez-le au poste de contrôle à votre arrivée, ainsi qu'à votre départ.",
            "",
            $"• Validité : jusqu'au {Format(n.ExpiresAt)}.",
            "• Ce QR Code est strictement personnel : merci de ne pas le partager.",
            "• Munissez-vous d'une pièce d'identité, qui pourra vous être demandée à l'entrée.",
            "• À présenter deux fois : à l'entrée ET à la sortie du site.",
            "",
            "Nous vous souhaitons une excellente visite.",
            "",
            "Cordialement,",
            $"{b.OrganizationName} — Contrôle des accès",
        };

        if (!string.IsNullOrWhiteSpace(b.SupportContact))
            lines.Add($"Une question ? {b.SupportContact}");

        lines.Add("");
        lines.Add($"— Message automatique envoyé via {b.ProductName}. Merci de ne pas répondre à cet email.");

        return string.Join("\n", lines);
    }

    /// <summary>Email HTML soigné (QR intégré via cid:qr), aux couleurs de la marque.</summary>
    public static string Html(VisitInvitationNotification n, NotificationBrandingOptions b)
    {
        var name = Enc(n.VisitorName);
        var intro = Enc(IntroSentence(n));
        var org = Enc(b.OrganizationName);
        var product = Enc(b.ProductName);
        var year = DateTimeOffset.UtcNow.Year;
        var expires = Enc(Format(n.ExpiresAt));
        var support = string.IsNullOrWhiteSpace(b.SupportContact)
            ? ""
            : $"<p style=\"margin:0 0 6px;color:#6b7784;font-size:13px;\">Une question ? " +
              $"<a href=\"mailto:{Enc(b.SupportContact!)}\" style=\"color:#0e2a3a;text-decoration:underline;\">{Enc(b.SupportContact!)}</a></p>";

        // Checklist en trois lignes, chacune avec une puce ronde ambre — un
        // <table> par ligne plutôt que flexbox/grid (non fiable dans les
        // clients mail, notamment Outlook desktop qui rend via Word).
        string ChecklistRow(string label, string text) => $@"
            <tr><td style=""padding:0 0 12px;"">
              <table role=""presentation"" cellpadding=""0"" cellspacing=""0""><tr>
                <td width=""24"" valign=""top"" style=""padding-right:10px;"">
                  <table role=""presentation"" width=""20"" height=""20"" cellpadding=""0"" cellspacing=""0"" style=""background:#f5a300;border-radius:50%;""><tr>
                    <td align=""center"" valign=""middle"" style=""font-size:12px;line-height:20px;color:#0e2a3a;font-weight:700;"">&#10003;</td>
                  </tr></table>
                </td>
                <td style=""font-size:14px;line-height:1.55;color:#3c4854;"">
                  <strong style=""color:#0e2a3a;"">{label}</strong> {text}
                </td>
              </tr></table>
            </td></tr>";

        return $@"<!DOCTYPE html>
<html lang=""fr""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""><meta name=""color-scheme"" content=""light""></head>
<body style=""margin:0;background:#eef0ec;font-family:'Segoe UI',Roboto,Arial,sans-serif;color:#10161d;"">
  <!-- Texte d'aperçu (masqué, améliore la ligne affichée dans la boîte de réception) -->
  <div style=""display:none;max-height:0;overflow:hidden;font-size:1px;line-height:1px;color:#eef0ec;"">
    Votre QR Code d'accès est prêt — présentez-le au poste de contrôle à l'arrivée et au départ.
  </div>

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
          <p style=""margin:0 0 16px;font-size:11px;font-weight:700;letter-spacing:1.2px;color:#f5a300;text-transform:uppercase;"">Invitation visiteur</p>
          <p style=""margin:0 0 14px;font-size:17px;color:#10161d;"">Bonjour {name},</p>
          <p style=""margin:0 0 26px;font-size:15px;line-height:1.6;color:#3c4854;"">{intro}</p>

          <!-- Carte QR façon billet -->
          <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#fbfaf6;border:1px solid #e4e1d6;border-radius:12px;"">
            <tr><td align=""center"" style=""padding:22px 20px 8px;"">
              <span style=""display:inline-block;padding:5px 14px;background:#0e2a3a;color:#f5a300;font-size:11px;font-weight:700;letter-spacing:.8px;text-transform:uppercase;border-radius:999px;"">À présenter au poste de contrôle</span>
            </td></tr>
            <tr><td align=""center"" style=""padding:16px 20px;"">
              <table role=""presentation"" cellpadding=""0"" cellspacing=""0"" style=""background:#ffffff;border-radius:10px;box-shadow:0 1px 4px rgba(14,42,58,.10);""><tr>
                <td style=""padding:14px;"">
                  <img src=""cid:qr"" alt=""QR Code d'accès"" width=""208"" height=""208"" style=""display:block;border-radius:4px;"">
                </td>
              </tr></table>
            </td></tr>
            <tr><td align=""center"" style=""padding:0 20px 20px;border-bottom:1px dashed #d5d1c2;"">
              <p style=""margin:0;font-size:12px;color:#8a8f7e;"">Valable à l'entrée et à la sortie du site</p>
            </td></tr>
          </table>

          <!-- Points clés -->
          <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""margin-top:24px;"">
            {ChecklistRow("Validité", $"jusqu'au {expires}.")}
            {ChecklistRow("Strictement personnel", "ce QR Code ne doit pas être partagé ni transféré.")}
            {ChecklistRow("Pièce d'identité", "à prévoir, elle pourra vous être demandée à l'entrée.")}
          </table>

          <p style=""margin:26px 0 6px;font-size:15px;color:#10161d;"">Nous vous souhaitons une excellente visite.</p>
          <p style=""margin:0 0 2px;font-size:15px;color:#10161d;"">Cordialement,</p>
          <p style=""margin:0 0 8px;font-size:15px;font-weight:600;color:#0e2a3a;"">{org} — Contrôle des accès</p>
        </td></tr>

        <!-- Pied -->
        <tr><td style=""padding:18px 28px 22px;border-top:1px solid #eceae0;background:#fbfaf6;"">
          {support}
          <p style=""margin:0 0 4px;color:#9aa7b2;font-size:12px;"">Message automatique envoyé via {product}. Merci de ne pas répondre à cet email.</p>
          <p style=""margin:0;color:#b7bdb0;font-size:11px;"">© {year} {org}</p>
        </td></tr>
      </table>
    </td></tr>
  </table>
</body></html>";
    }

    private static string IntroSentence(VisitInvitationNotification n) =>
        n.ScheduledAt is { } s
            ? $"Nous avons le plaisir de vous confirmer votre accès pour le rendez-vous du {Format(s)}."
            : "Nous avons le plaisir de vous confirmer votre accès visiteur, valable pendant 30 jours ouvrés.";

    private static string Format(DateTimeOffset dt) => dt.ToString("dddd d MMMM yyyy 'à' HH'h'mm",
        System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
