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
        var expires = Enc(Format(n.ExpiresAt));
        var support = string.IsNullOrWhiteSpace(b.SupportContact)
            ? ""
            : $"<p style=\"margin:0 0 4px;color:#6b7784;font-size:13px;\">Une question ? {Enc(b.SupportContact!)}</p>";

        return $@"<!DOCTYPE html>
<html lang=""fr""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""></head>
<body style=""margin:0;background:#f4f5f2;font-family:'Segoe UI',Arial,sans-serif;color:#10161d;"">
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f4f5f2;padding:24px 12px;"">
    <tr><td align=""center"">
      <table role=""presentation"" width=""560"" cellpadding=""0"" cellspacing=""0"" style=""max-width:560px;width:100%;background:#ffffff;border:1px solid #dce0db;border-radius:10px;overflow:hidden;"">
        <!-- En-tête -->
        <tr><td style=""background:#0e2a3a;border-bottom:3px solid #f5a300;padding:18px 28px;"">
          <span style=""font-size:20px;font-weight:700;letter-spacing:.5px;color:#ffffff;"">NOV<span style=""color:#f5a300;"">ACCÈS</span></span>
          <span style=""display:block;margin-top:2px;font-size:12px;color:rgba(255,255,255,.7);"">Contrôle des accès visiteurs</span>
        </td></tr>
        <!-- Corps -->
        <tr><td style=""padding:28px;"">
          <p style=""margin:0 0 14px;font-size:16px;"">Bonjour {name},</p>
          <p style=""margin:0 0 20px;font-size:15px;line-height:1.55;color:#3c4854;"">{intro}</p>

          <!-- QR -->
          <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"">
            <tr><td align=""center"" style=""padding:8px 0 20px;"">
              <img src=""cid:qr"" alt=""QR Code d'accès"" width=""220"" height=""220"" style=""display:block;border:1px solid #dce0db;border-radius:8px;padding:8px;background:#ffffff;"">
              <p style=""margin:10px 0 0;font-size:12px;color:#6b7784;"">Présentez ce QR Code au poste de contrôle</p>
            </td></tr>
          </table>

          <!-- Points clés -->
          <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f4f5f2;border-radius:8px;"">
            <tr><td style=""padding:14px 16px;font-size:14px;line-height:1.7;color:#3c4854;"">
              <strong style=""color:#0e2a3a;"">Validité :</strong> jusqu'au {expires}.<br>
              <strong style=""color:#0e2a3a;"">Personnel :</strong> ce QR Code ne doit pas être partagé.<br>
              <strong style=""color:#0e2a3a;"">À prévoir :</strong> une pièce d'identité, qui pourra vous être demandée à l'entrée.
            </td></tr>
          </table>

          <p style=""margin:22px 0 6px;font-size:15px;"">Nous vous souhaitons une excellente visite.</p>
          <p style=""margin:0 0 2px;font-size:15px;"">Cordialement,</p>
          <p style=""margin:0;font-size:15px;font-weight:600;color:#0e2a3a;"">{org} — Contrôle des accès</p>
        </td></tr>
        <!-- Pied -->
        <tr><td style=""padding:16px 28px;border-top:1px solid #dce0db;background:#fafbfa;"">
          {support}
          <p style=""margin:0;color:#9aa7b2;font-size:12px;"">Message automatique envoyé via {product}. Merci de ne pas répondre à cet email.</p>
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
