namespace NovAcces.Shared.Dtos;

/// <summary>Création d'un terminal : libellé + sites autorisés (un seul, la plupart du temps).</summary>
public sealed record CreateTerminalRequestDto(string Label, IReadOnlyList<string> SiteIds);

/// <summary>
/// Réponse de création : aucun secret n'est remis à cette étape.
/// Le secret opérationnel est délivré uniquement après activation QR.
/// </summary>
public sealed record CreateTerminalResponseDto(Guid Id, string Label)
{
    // Compatibilité source uniquement : jamais renseignée ni sérialisée.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKey { get; init; }
}

/// <summary>Terminal listé dans la console Admin — jamais la clé ni son empreinte.</summary>
/// <param name="PendingTicketExpiresAt">
/// Expiration du dernier ticket d'enrôlement non utilisé/non révoqué (null si
/// aucun n'a jamais été émis) — peut être dans le passé, à distinguer d'un
/// ticket encore valide (voir AdminTerminals.razor, StatusKey).
/// </param>
public sealed record TerminalSummaryDto(
    Guid Id, string Label, IReadOnlyList<string> SiteIds, bool IsActive, DateTimeOffset CreatedAt,
    bool IsEnrolled = false, DateTimeOffset? PendingTicketExpiresAt = null);

/// <summary>Terminal supprimé (archivé), pour la consultation en lecture seule (SuperAdmin).</summary>
public sealed record ArchivedTerminalSummaryDto(
    Guid Id, string Label, IReadOnlyList<string> SiteIds, DateTimeOffset DeletedAt, string? DeletedBy);

/// <summary>
/// Ticket temporaire affiché par la console d'administration sous forme de QR.
/// Le ticket brut n'est jamais stocké par l'API : seul son hash est conservé.
/// </summary>
public sealed record EnrollmentTicketResponseDto(
    Guid TerminalId,
    string Label,
    IReadOnlyList<string> SiteIds,
    string QrPayload,
    DateTimeOffset ExpiresAt)
{
    // Compatibilité avec les clients historiques : jamais renseigné ni sérialisé.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Ticket { get; init; }
}

/// <summary>Demande envoyée par un mobile après lecture du QR d'enrôlement.</summary>
/// <summary>
/// Activation d'un terminal par ticket QR.
///
/// <see cref="ProofSignature"/> est une PREUVE DE POSSESSION : signature ES256,
/// par la clé privée du device, de la chaîne « {Ticket}|{DeviceInstanceId} »
/// (UTF-8), encodée en Base64Url. Sans elle, la clé publique enregistrée
/// n'était qu'une chaîne décorative — n'importe qui interceptant le ticket
/// pouvait enrôler un appareil en déclarant une clé publique quelconque.
/// </summary>
public sealed record DeviceEnrollmentRequestDto(
    string Ticket,
    string DeviceInstanceId,
    string DevicePublicKeyPem,
    string ProofSignature = "");

/// <summary>
/// Réponse d'activation. La clé API est remise uniquement à cette étape puis
/// enregistrée dans le stockage sécurisé du mobile ; elle n'est jamais affichée
/// dans la console Web.
/// </summary>
public sealed record DeviceEnrollmentActivationDto(
    Guid TerminalId,
    string Label,
    IReadOnlyList<string> SiteIds,
    string ApiKey,
    DateTimeOffset EnrolledAt);