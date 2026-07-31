namespace NovAcces.Infrastructure.Notifications;

/// <summary>
/// Éléments de marque et de courtoisie repris dans les messages d'invitation
/// (email et WhatsApp). Configurables par déploiement (section "Notifications")
/// pour que Sigasécurité puisse personnaliser la signature par client/site.
/// </summary>
public sealed class NotificationBrandingOptions
{
    /// <summary>Nom affiché dans la signature (opérateur de sûreté).</summary>
    public string OrganizationName { get; set; } = "Sigasécurité";

    /// <summary>Nom du service/produit, pour la mention de bas de message.</summary>
    public string ProductName { get; set; } = "NovAcces";

    /// <summary>Contact d'assistance affiché en cas de question (optionnel).</summary>
    public string? SupportContact { get; set; }
}
