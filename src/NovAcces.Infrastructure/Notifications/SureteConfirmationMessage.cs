using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Notifications;

/// <summary>
/// Rédaction de l'email envoyé à la sûreté quand un agent demande une
/// validation sans QR/code (liste « Attendus »). Notification pure : l'email
/// ne contient AUCUN lien d'action (contrairement à un « magic link ») — la
/// décision (accepter/refuser) ne se prend que depuis le portail Sûreté
/// authentifié, jamais depuis la boîte mail (voir le choix documenté dans la
/// conversation avec le prestataire, 08/08/2026).
/// </summary>
internal static class SureteConfirmationMessage
{
    public static string Subject(SureteConfirmationRequestNotification n, NotificationBrandingOptions branding) =>
        $"[{branding.ProductName}] Confirmation requise — {n.VisitorName}";

    public static string PlainText(SureteConfirmationRequestNotification n, NotificationBrandingOptions branding)
    {
        var heure = n.RequestedAt.ToLocalTime().ToString("dd/MM/yyyy à HH:mm");
        var poste = string.IsNullOrWhiteSpace(n.CheckpointId) ? "" : $" (poste « {n.CheckpointId} »)";

        var signature = string.IsNullOrWhiteSpace(branding.SupportContact)
            ? $"\n\n--\n{branding.ProductName} — {branding.OrganizationName}"
            : $"\n\n--\n{branding.ProductName} — {branding.OrganizationName}\n{branding.SupportContact}";

        return $"Un agent demande une confirmation de {n.DirectionLabel} pour {n.VisitorName}{poste}, "
            + $"sans QR ni code de secours (le {heure}).\n\n"
            + "Connectez-vous au portail Sûreté pour accepter ou refuser cette demande. "
            + "Sans réponse de votre part, la demande expire automatiquement et équivaut à un refus — "
            + "le visiteur n'est jamais laissé entrer par défaut."
            + signature;
    }

    public static string Html(SureteConfirmationRequestNotification n, NotificationBrandingOptions branding)
    {
        var heure = n.RequestedAt.ToLocalTime().ToString("dd/MM/yyyy 'à' HH:mm");
        var name = EmailLayout.Enc(n.VisitorName);
        var poste = string.IsNullOrWhiteSpace(n.CheckpointId) ? "" : $" (poste « {EmailLayout.Enc(n.CheckpointId)} »)";

        var body = $@"
          <table role=""presentation"" cellpadding=""0"" cellspacing=""0"" style=""margin:0 0 18px;""><tr><td>
            {EmailLayout.Pill("Confirmation requise", "#fff4de", "#7a5200")}
          </td></tr></table>

          <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#fbfaf6;border:1px solid #e4e1d6;border-radius:10px;"">
            <tr><td style=""padding:16px 18px;font-size:14px;line-height:1.65;color:#3c4854;"">
              Un agent demande une confirmation de <strong style=""color:#0e2a3a;"">{EmailLayout.Enc(n.DirectionLabel)}</strong>
              pour <strong style=""color:#0e2a3a;"">{name}</strong>{poste}, sans QR ni code de secours (le {EmailLayout.Enc(heure)}).
              <br><br>
              Connectez-vous au portail Sûreté pour accepter ou refuser cette demande. Sans réponse de votre part,
              la demande expire automatiquement et équivaut à un refus — le visiteur n'est jamais laissé entrer par défaut.
            </td></tr>
          </table>";

        return EmailLayout.Wrap($"Confirmation requise — {n.VisitorName}", "Alerte sûreté", body, branding, branding.SupportContact);
    }
}
