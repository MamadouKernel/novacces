using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Security;

public sealed class QrSigningOptions
{
    /// <summary>
    /// Clé privée ECDSA P-256 au format PEM. NE JAMAIS committer de vraie valeur :
    /// en production, injectée via variable d'environnement ou secret manager
    /// (Azure Key Vault, Vault, ou fichier hors dépôt chargé au démarrage).
    /// Générer avec : openssl ecparam -genkey -name prime256v1 -noout -out qr-signing-key.pem
    /// </summary>
    public string PrivateKeyPem { get; set; } = default!;

    /// <summary>Clé publique correspondante — c'est elle qui est embarquée dans l'app agent MAUI.</summary>
    public string PublicKeyPem { get; set; } = default!;
}

/// <summary>
/// Signature ECDSA P-256 (algorithme ES256) des jetons QR et des listes hors
/// ligne. Choix motivé : nativement supporté par System.Security.Cryptography
/// (aucune dépendance cryptographique tierce à auditer), standard JWT
/// (RFC 7518), et vérifiable hors ligne — condition de REQ-SEC-06.
///
/// Le payload ne contient JAMAIS de donnée personnelle en clair (REQ-SEC-01) :
/// uniquement des identifiants opaques (Guid) et une expiration.
/// </summary>
public sealed class Es256QrSigningService : IQrSigningService, IDisposable
{
    private readonly ECDsa _signingKey;   // clé privée — signe uniquement côté serveur
    private readonly ECDsa _verifyingKey; // clé publique — utilisée aussi côté serveur ici,
                                           // et embarquée séparément dans l'app agent pour le mode dégradé

    public Es256QrSigningService(IOptions<QrSigningOptions> options)
    {
        var opts = options.Value;

        _signingKey = ECDsa.Create();
        _signingKey.ImportFromPem(opts.PrivateKeyPem);

        _verifyingKey = ECDsa.Create();
        _verifyingKey.ImportFromPem(opts.PublicKeyPem);
    }

    public string SignVisitToken(Guid visitId, Guid visitToken, DateTimeOffset expiresAt)
    {
        var payload = new VisitTokenPayload(visitId, visitToken, expiresAt.ToUnixTimeSeconds());
        return SignPayload(payload);
    }

    public QrVerificationResult VerifySignedToken(string signedPayload)
    {
        var payload = VerifyAndDeserialize<VisitTokenPayload>(signedPayload);
        if (payload is null)
            return new QrVerificationResult(false, null, null, null);

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.Exp);
        return new QrVerificationResult(true, payload.VisitId, payload.VisitToken, expiresAt);
    }

    public string SignDailyOfflineList(
        IReadOnlyCollection<OfflineListEntry> entries, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        var payload = new OfflineListPayload(
            entries.Select(e => new OfflineEntryDto(
                e.VisitId, e.VisitToken, e.ScheduledAt?.ToUnixTimeSeconds(), e.IsExcluded, e.IsOnSite)).ToArray(),
            issuedAt.ToUnixTimeSeconds(),
            expiresAt.ToUnixTimeSeconds());

        return SignPayload(payload);
    }

    public OfflineListVerificationResult VerifyDailyOfflineList(string signedList)
    {
        var payload = VerifyAndDeserialize<OfflineListPayload>(signedList);
        if (payload is null)
            return new OfflineListVerificationResult(false, true, Array.Empty<OfflineListEntry>());

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.Exp);
        var isExpired = DateTimeOffset.UtcNow > expiresAt;

        var entries = payload.Entries
            .Select(e => new OfflineListEntry(
                e.VisitId, e.VisitToken,
                e.ScheduledAtUnix.HasValue ? DateTimeOffset.FromUnixTimeSeconds(e.ScheduledAtUnix.Value) : null,
                e.IsExcluded, e.IsOnSite))
            .ToArray();

        return new OfflineListVerificationResult(true, isExpired, entries);
    }

    // ---- mécanique de signature commune (signature détachée, payload + signature en Base64Url) ----

    private string SignPayload<T>(T payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signature = _signingKey.SignData(json, HashAlgorithmName.SHA256);

        var envelope = new SignedEnvelope(
            Base64UrlEncode(json),
            Base64UrlEncode(signature));

        return JsonSerializer.Serialize(envelope);
    }

    private T? VerifyAndDeserialize<T>(string signedPayload)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<SignedEnvelope>(signedPayload);
            if (envelope is null) return default;

            var json = Base64UrlDecode(envelope.PayloadB64Url);
            var signature = Base64UrlDecode(envelope.SignatureB64Url);

            var isValid = _verifyingKey.VerifyData(json, signature, HashAlgorithmName.SHA256);
            if (!isValid) return default;

            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            // Toute anomalie de format = QR falsifié/corrompu = signature invalide.
            // Ne JAMAIS relancer : un QR malformé est un cas nominal de fraude,
            // pas une erreur technique (cf. REQ-SEC-05 : ça doit être journalisé
            // comme événement de sécurité, pas planter la requête).
            return default;
        }
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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
        _signingKey.Dispose();
        _verifyingKey.Dispose();
    }

    private sealed record SignedEnvelope(string PayloadB64Url, string SignatureB64Url);
    private sealed record VisitTokenPayload(Guid VisitId, Guid VisitToken, long Exp);
    private sealed record OfflineEntryDto(
        Guid VisitId, Guid VisitToken, long? ScheduledAtUnix, bool IsExcluded, bool IsOnSite = false);
    private sealed record OfflineListPayload(OfflineEntryDto[] Entries, long IssuedAt, long Exp);
}
