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

    private static string FormatDuree(int minutes)
    {
        if (minutes < 60) return $"{minutes} min";
        var heures = minutes / 60;
        var reste = minutes % 60;
        return reste == 0 ? $"{heures} h" : $"{heures} h {reste:00}";
    }
}
