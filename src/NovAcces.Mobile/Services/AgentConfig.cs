namespace NovAcces.Mobile.Services;

/// <summary>
/// Configuration du terminal agent. En production, renseignée à l'enrôlement du
/// terminal (jamais la clé privée de signature — seule la clé PUBLIQUE est
/// embarquée pour la vérification hors ligne).
/// </summary>
public sealed class AgentConfig
{
    /// <summary>URL de base de l'API NovAcces (ex. https://sicopa.novacces.ci).</summary>
    public string ApiBaseUrl { get; set; } = "https://localhost:54980";

    /// <summary>Clé API du terminal enrôlé (en-tête X-Api-Key).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Clé publique ES256 (PEM) pour la vérification hors ligne.</summary>
    public string PublicKeyPem { get; set; } = "";
}
