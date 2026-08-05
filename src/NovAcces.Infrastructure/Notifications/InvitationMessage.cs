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
        string.IsNullOrWhiteSpace(n.SiteLabel)
            ? "Votre QR Code d'accès visiteur — présentez-le au poste de contrôle"
            : $"Votre QR Code d'accès visiteur — {n.SiteLabel}";

    /// <summary>Version texte (base commune de l'email, sans HTML).</summary>
    public static string PlainText(VisitInvitationNotification n, NotificationBrandingOptions b)
    {
        var lines = new List<string>
        {
            $"Bonjour {n.VisitorName},",
            "",
            IntroSentence(n),
            "",
        };

        if (!string.IsNullOrWhiteSpace(n.SiteLabel))
            lines.Add($"• Site : {n.SiteLabel}");
        if (!string.IsNullOrWhiteSpace(n.VisitorCompany))
            lines.Add($"• Société : {n.VisitorCompany}");
        if (!string.IsNullOrWhiteSpace(n.Motif))
            lines.Add($"• Motif : {n.Motif}");
        if (lines[^1] != "")
            lines.Add("");

        lines.AddRange(new[]
        {
            "Vous trouverez votre QR Code d'accès personnel en pièce jointe (et ci-dessous s'il s'affiche). Présentez-le au poste de contrôle à votre arrivée, ainsi qu'à votre départ.",
            "",
            $"• Validité : jusqu'au {Format(n.ExpiresAt)}.",
            "• Ce QR Code est strictement personnel : merci de ne pas le partager.",
            "• Munissez-vous d'une pièce d'identité, qui pourra vous être demandée à l'entrée.",
            "• À présenter deux fois : à l'entrée ET à la sortie du site.",
            "",
            "Le QR ne s'affiche pas ou ne scanne pas ? Donnez ce code de secours à l'agent au poste de contrôle :",
            $"  {n.ManualCode}",
            "",
            "Nous vous souhaitons une excellente visite.",
            "",
            "Cordialement,",
            $"{b.OrganizationName} — Contrôle des accès",
        });

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
        var manualCode = Enc(n.ManualCode);
        var siteLabel = Enc(string.IsNullOrWhiteSpace(n.SiteLabel) ? "—" : n.SiteLabel);
        var rendezVous = Enc(n.ScheduledAt is { } s ? Format(s) : "Accès 30 jours ouvrés");
        var company = Enc(n.VisitorCompany);
        var motif = Enc(n.Motif);

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

        // Une cellule de la grille d'informations du billet (libellé en petites
        // capitales au-dessus de la valeur) — même idiome que le reste : table,
        // pas de flexbox, pour rester fiable sous Outlook desktop.
        string InfoCell(string label, string value, bool divider) => $@"
              <td width=""50%"" valign=""top"" style=""padding:0 0 4px {(divider ? "14px" : "0")};{(divider ? "border-left:1px solid #e4e1d6;padding-left:14px;" : "")}"">
                <p style=""margin:0 0 3px;font-size:10.5px;font-weight:700;letter-spacing:.8px;color:#9a9d8f;text-transform:uppercase;"">{label}</p>
                <p style=""margin:0;font-size:15px;font-weight:600;color:#0e2a3a;line-height:1.3;"">{value}</p>
              </td>";

        var infoGrid = $@"
            <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0""><tr>
              {InfoCell("Site", siteLabel, false)}
              {InfoCell("Rendez-vous", rendezVous, true)}
            </tr></table>";

        // Société/motif : quasi toujours renseignés (champs obligatoires à la
        // création), mais gardés défensifs — un email déjà en circulation avant
        // l'introduction de ces champs ne doit pas afficher une ligne vide.
        var hasSecondRow = !string.IsNullOrWhiteSpace(n.VisitorCompany) || !string.IsNullOrWhiteSpace(n.Motif);
        var infoGridRow2 = !hasSecondRow ? "" : $@"
            <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""margin-top:14px;""><tr>
              {InfoCell("Société", string.IsNullOrWhiteSpace(n.VisitorCompany) ? "—" : company, false)}
              {InfoCell("Motif", string.IsNullOrWhiteSpace(n.Motif) ? "—" : motif, true)}
            </tr></table>";

        var body = $@"
          <p style=""margin:0 0 14px;font-size:17px;color:#10161d;"">Bonjour {name},</p>
          <p style=""margin:0 0 26px;font-size:15px;line-height:1.6;color:#3c4854;"">{intro}</p>

          <!-- Carte façon billet d'embarquement : infos du rendez-vous, puis QR sous la perforation -->
          <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#fbfaf6;border:1px solid #e4e1d6;border-radius:12px;overflow:hidden;"">
            <tr><td style=""padding:20px 20px 18px;"">
              {infoGrid}
              {infoGridRow2}
            </td></tr>
            <tr><td style=""border-top:1px dashed #d5d1c2;line-height:0;font-size:0;"">&nbsp;</td></tr>
            <tr><td align=""center"" style=""padding:18px 20px 8px;"">
              {EmailLayout.Pill("À présenter au poste de contrôle", "#0e2a3a", "#f5a300")}
            </td></tr>
            <tr><td align=""center"" style=""padding:16px 20px;"">
              <table role=""presentation"" cellpadding=""0"" cellspacing=""0"" style=""background:#ffffff;border-radius:10px;box-shadow:0 1px 4px rgba(14,42,58,.10);""><tr>
                <td style=""padding:14px;"">
                  <img src=""cid:qr"" alt=""QR Code d'accès"" width=""208"" height=""208"" style=""display:block;border-radius:4px;"">
                </td>
              </tr></table>
            </td></tr>
            <tr><td align=""center"" style=""padding:0 20px 20px;"">
              <p style=""margin:0;font-size:12px;color:#8a8f7e;"">Valable à l'entrée et à la sortie du site</p>
            </td></tr>
          </table>

          <!-- Code de secours (alternative au QR) -->
          <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""margin-top:20px;background:#ffffff;border:1px dashed #c9d2d8;border-radius:10px;"">
            <tr><td style=""padding:16px 20px;"">
              <p style=""margin:0 0 8px;font-size:12px;color:#6b7784;line-height:1.5;"">Le QR ne s'affiche pas ou ne scanne pas&nbsp;? Donnez ce code de secours à l'agent au poste de contrôle&nbsp;:</p>
              <span style=""display:inline-block;padding:8px 16px;background:#0e2a3a;color:#ffffff;font-family:'Courier New',Courier,monospace;font-size:19px;font-weight:700;letter-spacing:2px;border-radius:6px;"">{manualCode}</span>
            </td></tr>
          </table>

          <!-- Points clés -->
          <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""margin-top:20px;"">
            {ChecklistRow("Validité", $"jusqu'au {expires}.")}
            {ChecklistRow("Strictement personnel", "ce QR Code ainsi que le code de secours sont personnels : merci de ne pas les partager au-delà de ce qui est nécessaire pour accéder au site.")}
            {ChecklistRow("Pièce d'identité", "à prévoir, elle pourra vous être demandée à l'entrée.")}
          </table>

          <p style=""margin:26px 0 6px;font-size:15px;color:#10161d;"">Nous vous souhaitons une excellente visite.</p>
          <p style=""margin:0 0 2px;font-size:15px;color:#10161d;"">Cordialement,</p>
          <p style=""margin:0 0 8px;font-size:15px;font-weight:600;color:#0e2a3a;"">{org} — Contrôle des accès</p>";

        return EmailLayout.Wrap(
            "Votre QR Code d'accès est prêt — présentez-le au poste de contrôle à l'arrivée et au départ.",
            "Invitation visiteur", body, b, b.SupportContact);
    }

    private static string IntroSentence(VisitInvitationNotification n)
    {
        var site = string.IsNullOrWhiteSpace(n.SiteLabel) ? "" : $" sur le site {n.SiteLabel}";
        return n.ScheduledAt is { } s
            ? $"Nous avons le plaisir de vous confirmer votre accès{site} pour le rendez-vous du {Format(s)}."
            : $"Nous avons le plaisir de vous confirmer votre accès visiteur{site}, valable pendant 30 jours ouvrés.";
    }

    private static string Format(DateTimeOffset dt) => dt.ToString("dddd d MMMM yyyy 'à' HH'h'mm",
        System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
