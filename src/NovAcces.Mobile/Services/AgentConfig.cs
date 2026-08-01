using System.Text.Json;
using Microsoft.Maui.Storage;
using NovAcces.Shared.Offline;

namespace NovAcces.Mobile.Services;

/// <summary>
/// Configuration du terminal agent. Les secrets et la clé privée de l'installation
/// sont conservés dans SecureStorage (Keychain iOS / Keystore Android).
/// </summary>
public sealed class AgentConfig
{
    private const string KeyBaseUrl = "agent_api_base_url";
    private const string KeyApiKey = "agent_api_key";
    private const string KeyPublicKey = "agent_public_key_pem";
    private const string KeyPublicKeyId = "agent_public_key_id";
    private const string KeyRetiredPublicKeys = "agent_retired_public_keys";
    private const string KeyDeviceId = "agent_device_instance_id";
    private const string KeyDevicePrivateKey = "agent_device_private_key_pem";
    private const string KeyDevicePublicKey = "agent_device_public_key_pem";

    public string ApiBaseUrl { get; set; } = "https://localhost";
    public string ApiKey { get; set; } = "";
    /// <summary>Clé publique ES256 COURANTE de l'API, utilisée pour le mode hors ligne.</summary>
    public string PublicKeyPem { get; set; } = "";

    /// <summary>Identifiant de la clé publique courante (« kid » de l'enveloppe signée).</summary>
    public string PublicKeyId { get; set; } = "current";

    /// <summary>
    /// Anciennes clés publiques encore acceptées côté serveur. Conservées pour
    /// que le mode dégradé continue de valider, pendant une rotation, les QR
    /// longue durée signés avant la bascule — sans elles, le terminal
    /// refuserait des visiteurs légitimes précisément pendant une coupure.
    /// </summary>
    public List<OfflineVerificationKey> RetiredPublicKeys { get; set; } = new();

    /// <summary>Toutes les clés à présenter au vérificateur hors ligne (courante + retirées).</summary>
    public IReadOnlyList<OfflineVerificationKey> VerificationKeys =>
        string.IsNullOrWhiteSpace(PublicKeyPem)
            ? Array.Empty<OfflineVerificationKey>()
            : new[] { new OfflineVerificationKey(PublicKeyId, PublicKeyPem) }
                .Concat(RetiredPublicKeys)
                .ToList();

    public string DeviceInstanceId { get; set; } = "";
    public string DevicePrivateKeyPem { get; set; } = "";
    public string DevicePublicKeyPem { get; set; } = "";

    /// <summary>
    /// Un ancien profil ayant seulement une clé API manuelle n'est plus considéré
    /// comme enrôlé : il repassera par le QR afin d'être lié à une installation.
    /// </summary>
    public bool IsEnrolled =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(PublicKeyPem)
        && Guid.TryParse(DeviceInstanceId, out _)
        && !string.IsNullOrWhiteSpace(DevicePrivateKeyPem)
        && !string.IsNullOrWhiteSpace(DevicePublicKeyPem);

    public async Task LoadFromSecureStorageAsync()
    {
        ApiBaseUrl = await SecureStorage.GetAsync(KeyBaseUrl) ?? "https://localhost";
        ApiKey = await SecureStorage.GetAsync(KeyApiKey) ?? "";
        PublicKeyPem = await SecureStorage.GetAsync(KeyPublicKey) ?? "";
        PublicKeyId = await SecureStorage.GetAsync(KeyPublicKeyId) ?? "current";
        RetiredPublicKeys = Deserialize(await SecureStorage.GetAsync(KeyRetiredPublicKeys));
        DeviceInstanceId = await SecureStorage.GetAsync(KeyDeviceId) ?? "";
        DevicePrivateKeyPem = await SecureStorage.GetAsync(KeyDevicePrivateKey) ?? "";
        DevicePublicKeyPem = await SecureStorage.GetAsync(KeyDevicePublicKey) ?? "";
    }

    public async Task SaveAsync()
    {
        await SecureStorage.SetAsync(KeyBaseUrl, ApiBaseUrl);
        await SecureStorage.SetAsync(KeyApiKey, ApiKey);
        await SecureStorage.SetAsync(KeyPublicKey, PublicKeyPem);
        await SecureStorage.SetAsync(KeyPublicKeyId, PublicKeyId);
        await SecureStorage.SetAsync(KeyRetiredPublicKeys, JsonSerializer.Serialize(RetiredPublicKeys));
        await SecureStorage.SetAsync(KeyDeviceId, DeviceInstanceId);
        await SecureStorage.SetAsync(KeyDevicePrivateKey, DevicePrivateKeyPem);
        await SecureStorage.SetAsync(KeyDevicePublicKey, DevicePublicKeyPem);
    }

    public void Clear()
    {
        ApiBaseUrl = "https://localhost";
        ApiKey = "";
        PublicKeyPem = "";
        PublicKeyId = "current";
        RetiredPublicKeys = new List<OfflineVerificationKey>();
        DeviceInstanceId = "";
        DevicePrivateKeyPem = "";
        DevicePublicKeyPem = "";
        SecureStorage.Remove(KeyBaseUrl);
        SecureStorage.Remove(KeyApiKey);
        SecureStorage.Remove(KeyPublicKey);
        SecureStorage.Remove(KeyPublicKeyId);
        SecureStorage.Remove(KeyRetiredPublicKeys);
        SecureStorage.Remove(KeyDeviceId);
        SecureStorage.Remove(KeyDevicePrivateKey);
        SecureStorage.Remove(KeyDevicePublicKey);
    }

    private static List<OfflineVerificationKey> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<OfflineVerificationKey>();
        try
        {
            return JsonSerializer.Deserialize<List<OfflineVerificationKey>>(json)
                   ?? new List<OfflineVerificationKey>();
        }
        catch (JsonException)
        {
            // Stockage corrompu : on repart sans clé retirée plutôt que de
            // bloquer le terminal au démarrage. La clé courante suffit au
            // fonctionnement nominal ; les retirées reviendront au prochain
            // rafraîchissement en ligne.
            return new List<OfflineVerificationKey>();
        }
    }
}