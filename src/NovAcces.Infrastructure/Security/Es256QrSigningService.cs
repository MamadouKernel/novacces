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

    /// <summary>Clé publique correspondante — c'est elle qui est embarquée dans le client mobile agent.</summary>
    public string PublicKeyPem { get; set; } = default!;

    /// <summary>
    /// Identifiant de la clé de signature courante. Il est EMBARQUÉ dans chaque
    /// enveloppe signée (champ « kid »), ce qui rend la rotation réellement
    /// possible : à la bascule, on déplace l'ancienne paire dans
    /// <see cref="RetiredVerificationKeys"/> et les QR déjà en circulation
    /// restent vérifiables jusqu'à leur expiration.
    /// </summary>
    public string KeyId { get; set; } = "current";

    /// <summary>
    /// Anciennes clés PUBLIQUES encore acceptées en vérification (jamais en
    /// signature), le temps que les QR émis avec elles expirent. Vide en
    /// fonctionnement nominal.
    /// </summary>
    public List<RetiredVerificationKey> RetiredVerificationKeys { get; set; } = new();
}

/// <summary>Ancienne clé publique conservée pour la vérification pendant une rotation.</summary>
public sealed class RetiredVerificationKey
{
    public string KeyId { get; set; } = default!;
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
    // Le service est enregistré en SINGLETON (une instance pour toute
    // l'application) et sollicité par des requêtes CONCURRENTES : plusieurs
    // postes de contrôle scannent en même temps pendant qu'un hôte crée une
    // visite. Or les membres d'INSTANCE de System.Security.Cryptography ne sont
    // pas garantis thread-safe (contrat documenté de .NET) — partager une même
    // ECDsa entre threads, c'est un comportement indéfini sur le chemin
    // cryptographique d'un système qui ouvre des portes.
    //
    // On isole donc une instance par thread. La clé PEM, elle, est immuable et
    // partageable sans risque ; seul l'objet cryptographique est cloisonné.
    // trackAllValues permet de libérer proprement toutes les instances créées.
    private readonly ThreadLocal<ECDsa> _signingKey;

    /// <summary>Clés acceptées EN VÉRIFICATION, indexées par identifiant de clé (kid).</summary>
    private readonly IReadOnlyDictionary<string, ThreadLocal<ECDsa>> _verificationKeys;

    private readonly string _currentKeyId;

    public Es256QrSigningService(IOptions<QrSigningOptions> options)
    {
        var opts = options.Value;

        _currentKeyId = string.IsNullOrWhiteSpace(opts.KeyId) ? "current" : opts.KeyId.Trim();

        var privateKeyPem = opts.PrivateKeyPem;
        _signingKey = new ThreadLocal<ECDsa>(() => ImportKey(privateKeyPem), trackAllValues: true);

        // La clé courante, plus les clés retirées encore tolérées le temps
        // qu'expirent les QR déjà distribués (rotation sans coupure).
        var verificationKeys = new Dictionary<string, ThreadLocal<ECDsa>>(StringComparer.Ordinal);
        AddVerificationKey(verificationKeys, _currentKeyId, opts.PublicKeyPem);
        foreach (var retired in opts.RetiredVerificationKeys ?? new List<RetiredVerificationKey>())
        {
            if (string.IsNullOrWhiteSpace(retired.KeyId) || string.IsNullOrWhiteSpace(retired.PublicKeyPem))
                continue;
            AddVerificationKey(verificationKeys, retired.KeyId.Trim(), retired.PublicKeyPem);
        }

        _verificationKeys = verificationKeys;

        // Échec au démarrage plutôt qu'au premier scan : une clé illisible doit
        // empêcher l'API de servir, pas produire des refus en série au poste.
        _ = _signingKey.Value;
        foreach (var key in _verificationKeys.Values)
            _ = key.Value;
    }

    private static void AddVerificationKey(
        IDictionary<string, ThreadLocal<ECDsa>> keys, string keyId, string publicKeyPem)
    {
        if (keys.ContainsKey(keyId))
            throw new InvalidOperationException(
                $"QrSigning : l'identifiant de clé « {keyId} » est déclaré deux fois.");

        keys[keyId] = new ThreadLocal<ECDsa>(() => ImportKey(publicKeyPem), trackAllValues: true);
    }

    private static ECDsa ImportKey(string pem)
    {
        var key = ECDsa.Create();

        // En production, la clé arrive par variable d'environnement (.env,
        // lu ligne à ligne par deploy.sh — voir son commentaire) : un PEM
        // multi-lignes n'y tient pas sur une ligne, donc la convention est
        // de l'y stocker avec des "\n" LITTÉRAUX (deux caractères) à la
        // place des retours à la ligne réels. Remplacement sans effet sur
        // un PEM déjà multi-lignes (user-secrets en dev, appsettings) :
        // aucune séquence "\n" littérale à y trouver.
        key.ImportFromPem(pem.Replace("\\n", "\n"));
        return key;
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
                e.VisitId, e.VisitToken, e.ScheduledAt?.ToUnixTimeSeconds(), e.IsExcluded, e.IsOnSite,
                e.VisitorName, e.Mode, e.WindowStart?.ToUnixTimeSeconds(), e.WindowEnd?.ToUnixTimeSeconds(), e.Status)).ToArray(),
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
                e.IsExcluded, e.IsOnSite, e.VisitorName, e.Mode,
                e.WindowStartUnix.HasValue ? DateTimeOffset.FromUnixTimeSeconds(e.WindowStartUnix.Value) : null,
                e.WindowEndUnix.HasValue ? DateTimeOffset.FromUnixTimeSeconds(e.WindowEndUnix.Value) : null,
                e.Status))
            .ToArray();

        return new OfflineListVerificationResult(true, isExpired, entries);
    }

    // ---- mécanique de signature commune (signature détachée, payload + signature en Base64Url) ----

    private string SignPayload<T>(T payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signature = _signingKey.Value!.SignData(json, HashAlgorithmName.SHA256);

        var envelope = new SignedEnvelope(
            Base64UrlEncode(json),
            Base64UrlEncode(signature),
            _currentKeyId);

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

            if (!VerifySignature(json, signature, envelope.KeyId)) return default;

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

    /// <summary>
    /// Un « kid » présent DÉSIGNE la clé à utiliser : un identifiant inconnu est
    /// un refus sec, jamais un repli qui essaierait les autres clés (sinon le
    /// champ ne servirait à rien et un attaquant pourrait le neutraliser en le
    /// falsifiant). Un « kid » absent correspond aux QR émis avant l'introduction
    /// du champ : on tente alors toutes les clés acceptées, ce qui reste sûr —
    /// c'est la signature, jamais le kid, qui fait foi.
    /// </summary>
    private bool VerifySignature(byte[] json, byte[] signature, string? keyId)
    {
        if (!string.IsNullOrWhiteSpace(keyId))
        {
            return _verificationKeys.TryGetValue(keyId, out var key)
                && key.Value!.VerifyData(json, signature, HashAlgorithmName.SHA256);
        }

        foreach (var key in _verificationKeys.Values)
        {
            if (key.Value!.VerifyData(json, signature, HashAlgorithmName.SHA256))
                return true;
        }

        return false;
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
        DisposeAll(_signingKey);
        foreach (var key in _verificationKeys.Values)
            DisposeAll(key);
    }

    private static void DisposeAll(ThreadLocal<ECDsa> holder)
    {
        foreach (var instance in holder.Values)
            instance.Dispose();
        holder.Dispose();
    }

    // KeyId est nullable pour rester compatible avec les enveloppes émises avant
    // son introduction (QR 30 jours encore en circulation lors du déploiement).
    private sealed record SignedEnvelope(
        string PayloadB64Url, string SignatureB64Url, string? KeyId = null);
    private sealed record VisitTokenPayload(Guid VisitId, Guid VisitToken, long Exp);
    private sealed record OfflineEntryDto(
        Guid VisitId, Guid VisitToken, long? ScheduledAtUnix, bool IsExcluded, bool IsOnSite = false,
        string? VisitorName = null, string? Mode = null, long? WindowStartUnix = null,
        long? WindowEndUnix = null, string? Status = null);
    private sealed record OfflineListPayload(OfflineEntryDto[] Entries, long IssuedAt, long Exp);
}
