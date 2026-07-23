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
}
