namespace NovAcces.Application.Abstractions;

/// <summary>
/// Signature et vérification cryptographique des jetons QR (ECDSA P-256 / ES256).
/// La clé privée ne quitte jamais le serveur (REQ-SEC-01, REQ-SEC-04 du CDC).
/// Implémentation réelle : NovAcces.Infrastructure.Security.Es256QrSigningService.
/// </summary>
public interface IQrSigningService
{
    /// <summary>
    /// Produit le contenu signé à encoder dans le QR : identifiant de visite
    /// chiffré + expiration + signature. Aucune donnée personnelle en clair.
    /// </summary>
    string SignVisitToken(Guid visitId, Guid visitToken, DateTimeOffset expiresAt);

    /// <summary>
    /// Vérifie la signature d'un QR scanné. Ne fait AUCUN accès réseau ni base :
    /// c'est une opération purement cryptographique, donc utilisable telle
    /// quelle en mode dégradé (REQ-SEC-06).
    /// </summary>
    QrVerificationResult VerifySignedToken(string signedPayload);

    /// <summary>
    /// Signe la liste des QR valides du jour pour le mode dégradé (REQ-SEC-06.a).
    /// </summary>
    string SignDailyOfflineList(IReadOnlyCollection<OfflineListEntry> entries, DateTimeOffset issuedAt, DateTimeOffset expiresAt);

    /// <summary>Vérifie l'intégrité d'une liste hors ligne avant tout usage local.</summary>
    OfflineListVerificationResult VerifyDailyOfflineList(string signedList);
}

public sealed record QrVerificationResult(bool IsValid, Guid? VisitId, Guid? VisitToken, DateTimeOffset? ExpiresAt);

public sealed record OfflineListEntry(
    Guid VisitId,
    Guid VisitToken,
    DateTimeOffset? ScheduledAt,
    bool IsExcluded,
    bool IsOnSite = false,
    string? VisitorName = null,
    string? Mode = null,
    DateTimeOffset? WindowStart = null,
    DateTimeOffset? WindowEnd = null,
    string? Status = null);

public sealed record OfflineListVerificationResult(bool IsValid, bool IsExpired, IReadOnlyCollection<OfflineListEntry> Entries);
