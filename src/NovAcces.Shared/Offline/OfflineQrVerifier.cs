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
/// { PayloadB64Url, SignatureB64Url }, payload JSON signé en ECDSA P-256 / SHA-256.
/// Toute divergence casserait la vérification hors ligne — d'où les tests de
/// compatibilité croisée (NovAcces.UnitTests/Security).
/// </summary>
public sealed class OfflineQrVerifier : IDisposable
{
    private readonly ECDsa _publicKey;

    public OfflineQrVerifier(string publicKeyPem)
    {
        _publicKey = ECDsa.Create();
        _publicKey.ImportFromPem(publicKeyPem);
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

            if (!_publicKey.VerifyData(json, signature, HashAlgorithmName.SHA256))
                return default;

            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            // Toute anomalie de format = QR/liste falsifié ou corrompu = invalide.
            return default;
        }
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

    public void Dispose() => _publicKey.Dispose();

    // ---- Structures miroir du format serveur (ne pas modifier isolément) ----
    private sealed record SignedEnvelope(string PayloadB64Url, string SignatureB64Url);
    private sealed record VisitTokenPayload(Guid VisitId, Guid VisitToken, long Exp);
    private sealed record OfflineEntryDto(
        Guid VisitId, Guid VisitToken, long? ScheduledAtUnix, bool IsExcluded, bool IsOnSite = false);
    private sealed record OfflineListPayload(OfflineEntryDto[] Entries, long IssuedAt, long Exp);
}

/// <summary>Résultat de vérification d'un jeton QR hors ligne.</summary>
public sealed record OfflineTokenResult(bool IsValid, Guid? VisitId, Guid? VisitToken, DateTimeOffset? ExpiresAt);

/// <summary>Résultat de vérification de la liste hors-ligne du jour.</summary>
public sealed record OfflineListResult(bool IsValid, bool IsExpired, IReadOnlyList<OfflineListItem> Entries);

public sealed record OfflineListItem(
    Guid VisitId, Guid VisitToken, DateTimeOffset? ScheduledAt, bool IsExcluded, bool IsOnSite = false);
