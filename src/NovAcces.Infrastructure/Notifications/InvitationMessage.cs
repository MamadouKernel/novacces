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
        var expires = Enc(Format(n.ExpiresAt));

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

        var body = $@"
          <p style=""margin:0 0 14px;font-size:17px;color:#10161d;"">Bonjour {name},</p>
          <p style=""margin:0 0 26px;font-size:15px;line-height:1.6;color:#3c4854;"">{intro}</p>

          <!-- Carte QR façon billet -->
          <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#fbfaf6;border:1px solid #e4e1d6;border-radius:12px;"">
            <tr><td align=""center"" style=""padding:22px 20px 8px;"">
              {EmailLayout.Pill("À présenter au poste de contrôle", "#0e2a3a", "#f5a300")}
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
          <p style=""margin:0 0 8px;font-size:15px;font-weight:600;color:#0e2a3a;"">{org} — Contrôle des accès</p>";

        return EmailLayout.Wrap(
            "Votre QR Code d'accès est prêt — présentez-le au poste de contrôle à l'arrivée et au départ.",
            "Invitation visiteur", body, b, b.SupportContact);
    }

    private static string IntroSentence(VisitInvitationNotification n) =>
        n.ScheduledAt is { } s
            ? $"Nous avons le plaisir de vous confirmer votre accès pour le rendez-vous du {Format(s)}."
            : "Nous avons le plaisir de vous confirmer votre accès visiteur, valable pendant 30 jours ouvrés.";

    private static string Format(DateTimeOffset dt) => dt.ToString("dddd d MMMM yyyy 'à' HH'h'mm",
        System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
