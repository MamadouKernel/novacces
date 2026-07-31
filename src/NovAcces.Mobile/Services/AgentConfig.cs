using Microsoft.Maui.Storage;

namespace NovAcces.Mobile.Services;

/// <summary>
/// Configuration du terminal agent, chargée depuis le STOCKAGE SÉCURISÉ du
/// terminal (SecureStorage : Keychain iOS / KeyStore Android), renseignée à
/// l'enrôlement — plus aucun secret en dur dans le code. Jamais la clé privée de
/// signature : seule la clé PUBLIQUE est embarquée, pour la vérification hors ligne.
/// </summary>
public sealed class AgentConfig
{
    private const string KeyBaseUrl = "agent_api_base_url";
    private const string KeyApiKey = "agent_api_key";
    private const string KeyPublicKey = "agent_public_key_pem";

    /// <summary>URL de base de l'API NovAcces (ex. https://sicopa.novacces.ci).</summary>
    public string ApiBaseUrl { get; set; } = "https://localhost";

    /// <summary>Clé API du terminal enrôlé (en-tête X-Api-Key).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Clé publique ES256 (PEM) pour la vérification hors ligne.</summary>
    public string PublicKeyPem { get; set; } = "";

    /// <summary>Vrai si le terminal a été enrôlé (clé API + clé publique présentes).</summary>
    public bool IsEnrolled => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(PublicKeyPem);

    /// <summary>
    /// Charge la configuration depuis le stockage sécurisé DANS cette instance.
    /// À appeler en asynchrone (jamais en bloquant sur le thread principal :
    /// SecureStorage figerait l'UI au démarrage sur Android).
    /// </summary>
    public async Task LoadFromSecureStorageAsync()
    {
        ApiBaseUrl = await SecureStorage.GetAsync(KeyBaseUrl) ?? "https://localhost";
        ApiKey = await SecureStorage.GetAsync(KeyApiKey) ?? "";
        PublicKeyPem = await SecureStorage.GetAsync(KeyPublicKey) ?? "";
    }

    /// <summary>Enrôlement : écrit les paramètres du terminal dans le stockage sécurisé.</summary>
    public async Task SaveAsync()
    {
        await SecureStorage.SetAsync(KeyBaseUrl, ApiBaseUrl);
        await SecureStorage.SetAsync(KeyApiKey, ApiKey);
        await SecureStorage.SetAsync(KeyPublicKey, PublicKeyPem);
    }
}
