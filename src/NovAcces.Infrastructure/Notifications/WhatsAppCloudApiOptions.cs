namespace NovAcces.Infrastructure.Notifications;

/// <summary>
/// Paramètres WhatsApp Business Platform (Meta Cloud API), retenue en
/// remplacement du SMS (accord-commercial.md). En production, ces valeurs
/// viennent d'une variable d'environnement ou d'un secret manager — jamais
/// commitées. Le template doit être pré-approuvé par Meta (catégorie
/// "Utility" recommandée), avec un composant "header" de type image (pour
/// le QR) et un composant "body" à deux paramètres texte.
/// </summary>
public sealed class WhatsAppCloudApiOptions
{
    public string ApiBaseUrl { get; set; } = "https://graph.facebook.com/v20.0";
    public string PhoneNumberId { get; set; } = default!;
    public string AccessToken { get; set; } = default!;
    public string TemplateName { get; set; } = default!;
    public string TemplateLanguageCode { get; set; } = "fr";

    /// <summary>
    /// Mode d'envoi :
    /// - "Image" (défaut) : le QR est envoyé en image avec une légende
    ///   rédigée (conforme à l'accord « QR envoyé en image dans la
    ///   conversation »). Adapté quand le visiteur a déjà une conversation
    ///   ouverte (fenêtre de 24 h) ou pour les tests.
    /// - "Template" : message basé sur un template Meta pré-approuvé
    ///   (obligatoire pour un premier contact hors fenêtre de 24 h).
    /// </summary>
    public string SendMode { get; set; } = "Image";
}
