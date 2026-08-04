using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Notifications;

/// <summary>
/// Rédaction des messages envoyés à l'HÔTE quand son visiteur arrive, repart,
/// dépasse sa durée, ou quand une anomalie est détectée (§1.3, §1.6, §2, §7).
///
/// Deux règles tenues ici :
///  - on ne communique JAMAIS à l'hôte le détail d'une décision de sûreté
///    (motif d'exclusion, etc.) : il apprend ce qui concerne SON visiteur,
///    rien de plus ;
///  - pour une suspicion de copie, le message demande une VÉRIFICATION et
///    n'accuse personne : à ce stade on ne sait pas qui, du titulaire ou d'un
///    tiers, s'est présenté en premier.
/// </summary>
internal static class HostEventMessage
{
    public static string Subject(HostEventNotification n, NotificationBrandingOptions branding) => n.Kind switch
    {
        HostEventKind.Arrival => $"[{branding.ProductName}] {n.VisitorName} est arrivé(e)",
        HostEventKind.Departure => $"[{branding.ProductName}] {n.VisitorName} a quitté le site",
        HostEventKind.SuspectedDuplicate =>
            $"[{branding.ProductName}] Vérification requise — {n.VisitorName}",
        HostEventKind.Overstay =>
            $"[{branding.ProductName}] {n.VisitorName} dépasse la durée prévue",
        _ => $"[{branding.ProductName}] Événement visiteur",
    };

    public static string PlainText(HostEventNotification n, NotificationBrandingOptions branding)
    {
        var heure = n.OccurredAt.ToLocalTime().ToString("dd/MM/yyyy à HH:mm");

        var corps = n.Kind switch
        {
            HostEventKind.Arrival =>
                $"Votre visiteur {n.VisitorName} est entré sur le site le {heure}.",

            HostEventKind.Departure =>
                $"Votre visiteur {n.VisitorName} a quitté le site le {heure}"
                + (n.PresenceMinutes is { } p ? $", après {FormatDuree(p)} de présence." : ".")
                + (n.OverstayMinutes is > 0 ? $"\nDurée prévue dépassée de {FormatDuree(n.OverstayMinutes.Value)}." : ""),

            HostEventKind.SuspectedDuplicate =>
                $"Le QR de {n.VisitorName} a été présenté à l'entrée le {heure}, alors que cette "
                + "personne est déjà enregistrée comme présente sur le site.\n\n"
                + "Merci de vérifier que votre visiteur est bien arrivé. L'accès a été refusé et "
                + "le poste de garde a été alerté.",

            HostEventKind.Overstay =>
                $"Votre visiteur {n.VisitorName} est toujours présent et dépasse la durée de visite "
                + $"prévue de {FormatDuree(n.OverstayMinutes ?? 0)}"
                + (n.OverstayLevel is >= 3
                    ? ".\n\nCe dépassement est signalé comme événement de sécurité : une vérification "
                      + "physique par un agent est recommandée."
                    : ".\n\nSi la visite se prolonge normalement, aucune action n'est requise."),

            _ => $"Événement concernant {n.VisitorName}, le {heure}.",
        };

        var signature = string.IsNullOrWhiteSpace(branding.SupportContact)
            ? $"\n\n--\n{branding.ProductName} — {branding.OrganizationName}"
            : $"\n\n--\n{branding.ProductName} — {branding.OrganizationName}\n{branding.SupportContact}";

        return $"Bonjour {n.Host.DisplayName},\n\n{corps}{signature}";
    }

    /// <summary>
    /// Email HTML soigné. Le badge de statut utilise la palette d'état DÉJÀ
    /// en place dans la console (pastilles pi-*, voir tailwind.config.js) —
    /// pas la couleur de marque (ambre), volontairement réservée à l'accent
    /// décoratif : ici la couleur porte un sens (sur site / terminé / alerte).
    /// </summary>
    public static string Html(HostEventNotification n, NotificationBrandingOptions branding)
    {
        var heure = n.OccurredAt.ToLocalTime().ToString("dd/MM/yyyy 'à' HH:mm");
        var name = EmailLayout.Enc(n.VisitorName);

        var (pillLabel, pillBg, pillFg, eyebrow) = n.Kind switch
        {
            HostEventKind.Arrival => ("Arrivée confirmée", "#e3eef9", "#1b5e9e", "Notification visiteur"),
            HostEventKind.Departure => ("Visite terminée", "#e4f4e9", "#0f8a3d", "Notification visiteur"),
            HostEventKind.SuspectedDuplicate => ("Vérification requise", "#fbe9e9", "#c92a2a", "Alerte sûreté"),
            HostEventKind.Overstay when n.OverstayLevel is >= 3 => ("Événement de sécurité", "#fbe9e9", "#c92a2a", "Alerte sûreté"),
            HostEventKind.Overstay => ("Dépassement de durée", "#fff4de", "#7a5200", "Notification visiteur"),
            _ => ("Événement visiteur", "#eef0ec", "#3c4854", "Notification visiteur"),
        };

        var corps = n.Kind switch
        {
            HostEventKind.Arrival =>
                $"Votre visiteur <strong style=\"color:#0e2a3a;\">{name}</strong> est entré sur le site le {EmailLayout.Enc(heure)}.",

            HostEventKind.Departure =>
                $"Votre visiteur <strong style=\"color:#0e2a3a;\">{name}</strong> a quitté le site le {EmailLayout.Enc(heure)}"
                + (n.PresenceMinutes is { } p ? $", après {EmailLayout.Enc(FormatDuree(p))} de présence." : ".")
                + (n.OverstayMinutes is > 0
                    ? $"<br>Durée prévue dépassée de {EmailLayout.Enc(FormatDuree(n.OverstayMinutes.Value))}."
                    : ""),

            HostEventKind.SuspectedDuplicate =>
                $"Le QR de <strong style=\"color:#0e2a3a;\">{name}</strong> a été présenté à l'entrée le {EmailLayout.Enc(heure)}, "
                + "alors que cette personne est déjà enregistrée comme présente sur le site."
                + "<br><br>Merci de vérifier que votre visiteur est bien arrivé. L'accès a été refusé et le poste de garde a été alerté.",

            HostEventKind.Overstay =>
                $"Votre visiteur <strong style=\"color:#0e2a3a;\">{name}</strong> est toujours présent et dépasse la durée de "
                + $"visite prévue de {EmailLayout.Enc(FormatDuree(n.OverstayMinutes ?? 0))}"
                + (n.OverstayLevel is >= 3
                    ? "<br><br>Ce dépassement est signalé comme événement de sécurité : une vérification physique par un agent est recommandée."
                    : "<br><br>Si la visite se prolonge normalement, aucune action n'est requise."),

            _ => $"Événement concernant <strong style=\"color:#0e2a3a;\">{name}</strong>, le {EmailLayout.Enc(heure)}.",
        };

        var body = $@"
          <p style=""margin:0 0 14px;font-size:17px;color:#10161d;"">Bonjour {EmailLayout.Enc(n.Host.DisplayName)},</p>

          <table role=""presentation"" cellpadding=""0"" cellspacing=""0"" style=""margin:0 0 18px;""><tr><td>
            {EmailLayout.Pill(pillLabel, pillBg, pillFg)}
          </td></tr></table>

          <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#fbfaf6;border:1px solid #e4e1d6;border-radius:10px;"">
            <tr><td style=""padding:16px 18px;font-size:14px;line-height:1.65;color:#3c4854;"">{corps}</td></tr>
          </table>";

        return EmailLayout.Wrap($"{pillLabel} — {n.VisitorName}", eyebrow, body, branding, branding.SupportContact);
    }

    private static string FormatDuree(int minutes)
    {
        if (minutes < 60) return $"{minutes} min";
        var heures = minutes / 60;
        var reste = minutes % 60;
        return reste == 0 ? $"{heures} h" : $"{heures} h {reste:00}";
    }
}
