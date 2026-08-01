using System.Security.Cryptography;
using System.Text.Json;

namespace NovAcces.Shared.Offline;

/// <summary>
/// Vérification ES256 HORS LIGNE des QR et de la liste du jour, avec la seule
/// CLÉ PUBLIQUE. Destiné à être embarqué dans le client mobile agent (React Native) : la clé
/// privée ne quitte jamais le serveur. C'est une opération purement mathématique
/// (aucune base, aucun réseau) — le socle du mode dégradé (§5, §6).
///
/// Le format reproduit EXACTEMENT celui de la signature serveur
/// (Infrastructure/Security/Es256QrSigningService) : enveloppe détachée
/// { PayloadB64Url, SignatureB64Url, KeyId }, payload JSON signé en
/// ECDSA P-256 / SHA-256. Toute divergence casserait la vérification hors ligne
/// — d'où les tests de compatibilité croisée (NovAcces.UnitTests/Security).
///
/// Plusieurs clés peuvent être acceptées simultanément : pendant une rotation,
/// le terminal doit pouvoir vérifier hors ligne aussi bien les QR fraîchement
/// émis que ceux signés avec l'ancienne clé et encore en circulation (accès
/// 30 jours). Refuser ces derniers reviendrait à bloquer des visiteurs
/// légitimes au poste, précisément pendant une coupure réseau.
/// </summary>
public sealed class OfflineQrVerifier : IDisposable
{
    private readonly IReadOnlyDictionary<string, ECDsa> _publicKeys;

    public OfflineQrVerifier(string publicKeyPem)
        : this(new[] { new OfflineVerificationKey("current", publicKeyPem) })
    {
    }

    public OfflineQrVerifier(IEnumerable<OfflineVerificationKey> keys)
    {
        var imported = new Dictionary<string, ECDsa>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key.KeyId) || string.IsNullOrWhiteSpace(key.PublicKeyPem))
                continue;

            var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(key.PublicKeyPem);
            imported[key.KeyId] = ecdsa;
        }

        if (imported.Count == 0)
            throw new ArgumentException("Aucune clé publique de vérification exploitable.", nameof(keys));

        _publicKeys = imported;
    }

    /// <summary>Vérifie un jeton QR signé. Renvoie l'identité de visite si valide.</summary>
    public OfflineTokenResult VerifyToken(string signedPayload)
    {
        var payload = VerifyAndDeserialize<VisitTokenPayload>(signedPayload);
        if (payload is null)
            return new OfflineTokenResult(false, null, null, null);

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.Exp);
        return new OfflineTokenResult(true, payload.VisitId, payload.VisitToken, expiresAt);
    }

    /// <summary>
    /// Vérifie la liste hors-ligne du jour (signature + TTL). isExpired indique
    /// que le TTL est dépassé : plus aucune validation locale possible (§6.3).
    /// </summary>
    public OfflineListResult VerifyDailyList(string signedList, DateTimeOffset now)
    {
        var payload = VerifyAndDeserialize<OfflineListPayload>(signedList);
        if (payload is null)
            return new OfflineListResult(false, true, Array.Empty<OfflineListItem>());

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.Exp);
        var isExpired = now > expiresAt;

        var items = payload.Entries.Select(e => new OfflineListItem(
            e.VisitId, e.VisitToken,
            e.ScheduledAtUnix.HasValue ? DateTimeOffset.FromUnixTimeSeconds(e.ScheduledAtUnix.Value) : null,
            e.IsExcluded, e.IsOnSite)).ToArray();

        return new OfflineListResult(true, isExpired, items);
    }

    private T? VerifyAndDeserialize<T>(string signedPayload)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<SignedEnvelope>(signedPayload);
            if (envelope is null) return default;

            var json = Base64UrlDecode(envelope.PayloadB64Url);
            var signature = Base64UrlDecode(envelope.SignatureB64Url);

            if (!VerifySignature(json, signature, envelope.KeyId))
                return default;

            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            // Toute anomalie de format = QR/liste falsifié ou corrompu = invalide.
            return default;
        }
    }

    /// <summary>
    /// Même règle que le serveur : un « kid » présent DÉSIGNE la clé (un
    /// identifiant inconnu est un refus sec, jamais un repli sur les autres
    /// clés) ; un « kid » absent correspond aux QR antérieurs au champ et fait
    /// essayer toutes les clés acceptées. C'est la signature qui fait foi.
    /// </summary>
    private bool VerifySignature(byte[] json, byte[] signature, string? keyId)
    {
        if (!string.IsNullOrWhiteSpace(keyId))
        {
            return _publicKeys.TryGetValue(keyId, out var key)
                && key.VerifyData(json, signature, HashAlgorithmName.SHA256);
        }

        foreach (var key in _publicKeys.Values)
        {
            if (key.VerifyData(json, signature, HashAlgorithmName.SHA256))
                return true;
        }

        return false;
    }

    private static byte[] Base64UrlDecode(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    public void Dispose()
    {
        foreach (var key in _publicKeys.Values)
            key.Dispose();
    }

    // ---- Structures miroir du format serveur (ne pas modifier isolément) ----
    private sealed record SignedEnvelope(
        string PayloadB64Url, string SignatureB64Url, string? KeyId = null);
    private sealed record VisitTokenPayload(Guid VisitId, Guid VisitToken, long Exp);
    private sealed record OfflineEntryDto(
        Guid VisitId, Guid VisitToken, long? ScheduledAtUnix, bool IsExcluded, bool IsOnSite = false);
    private sealed record OfflineListPayload(OfflineEntryDto[] Entries, long IssuedAt, long Exp);
}

/// <summary>Clé publique acceptée en vérification hors ligne, avec son identifiant.</summary>
public sealed record OfflineVerificationKey(string KeyId, string PublicKeyPem);

/// <summary>Résultat de vérification d'un jeton QR hors ligne.</summary>
public sealed record OfflineTokenResult(bool IsValid, Guid? VisitId, Guid? VisitToken, DateTimeOffset? ExpiresAt);

/// <summary>Résultat de vérification de la liste hors-ligne du jour.</summary>
public sealed record OfflineListResult(bool IsValid, bool IsExpired, IReadOnlyList<OfflineListItem> Entries);

public sealed record OfflineListItem(
    Guid VisitId, Guid VisitToken, DateTimeOffset? ScheduledAt, bool IsExcluded, bool IsOnSite = false);
