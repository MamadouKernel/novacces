namespace NovAcces.Api.Configuration;

/// <summary>
/// Refuse un démarrage de production avec des secrets ou endpoints par défaut.
/// Les valeurs de développement restent injectées par user-secrets/fixtures.
/// </summary>
public static class ProductionConfigurationValidator
{
    public static void Validate(IConfiguration configuration)
    {
        Require(configuration.GetConnectionString("Postgres"), "ConnectionStrings:Postgres");
        Require(configuration["Jwt:SigningKey"], "Jwt:SigningKey", minimumLength: 32);
        Require(configuration["QrSigning:PrivateKeyPem"], "QrSigning:PrivateKeyPem");
        Require(configuration["QrSigning:PublicKeyPem"], "QrSigning:PublicKeyPem");

        var baseUrl = configuration["Api:PublicBaseUrl"];
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Api:PublicBaseUrl doit être une URL HTTPS absolue sans query ni fragment en production.");

        // TEMPORAIRE (02/08/2026, Mamadou) : assoupli le temps de créer le compte
        // Brevo et récupérer les identifiants SMTP réels. Email = seul canal de
        // notification du produit (WhatsApp abandonné) — à RESTAURER (décommenter
        // les 4 lignes ci-dessous) avant tout test avec de vrais visiteurs/pilote.
        // Require(configuration["Smtp:Host"], "Smtp:Host");
        // Require(configuration["Smtp:Username"], "Smtp:Username");
        // Require(configuration["Smtp:Password"], "Smtp:Password");
        // Require(configuration["Smtp:FromAddress"], "Smtp:FromAddress");
    }

    private static void Require(string? value, string key, int minimumLength = 1)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Length < minimumLength)
            throw new InvalidOperationException($"Configuration de production manquante ou invalide : {key}.");
    }
}