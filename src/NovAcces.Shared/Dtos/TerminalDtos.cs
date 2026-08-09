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
    bool IsEnrolled = false, DateTimeOffset? PendingTicketExpiresAt = null,
    string? CheckpointId = null, string? DeviceModel = null);

/// <summary>Affectation (Admin) d'un terminal à un poste — un poste peut regrouper plusieurs terminaux. CheckpointId null/vide retire l'affectation.</summary>
public sealed record SetTerminalCheckpointRequestDto(string? CheckpointId);

/// <summary>
/// Création d'un ticket de POSTE (09/08/2026) : libellé + sites + poste
/// optionnel — AUCUN terminal précréé, contrairement à CreateTerminalRequestDto.
/// Le ticket qui en résulte est réutilisable (voir EnrollmentTicketResponseDto),
/// chaque scan crée son propre terminal sous ce gabarit.
/// </summary>
public sealed record CreatePosteEnrollmentTicketRequestDto(
    string Label, IReadOnlyList<string> SiteIds, string? CheckpointId = null);

/// <summary>Modèle/OS de l'appareil, remonté par l'app agent après connexion (expo-device) — purement informatif.</summary>
public sealed record AgentDeviceInfoRequestDto(string DeviceModel);

/// <summary>Terminal supprimé (archivé), pour la consultation en lecture seule (SuperAdmin).</summary>
public sealed record ArchivedTerminalSummaryDto(
    Guid Id, string Label, IReadOnlyList<string> SiteIds, DateTimeOffset DeletedAt, string? DeletedBy);

/// <summary>
/// Ticket temporaire affiché par la console d'administration sous forme de QR.
/// Le ticket brut n'est jamais stocké par l'API : seul son hash est conservé.
/// <paramref name="ManualCode"/> est une alternative de secours au QR (même
/// ticket, même expiration) si la caméra du terminal est hors service.
/// <paramref name="TerminalId"/> est nul pour un ticket de POSTE (réutilisable,
/// pas encore lié à un terminal précis avant le premier scan).
/// </summary>
public sealed record EnrollmentTicketResponseDto(
    Guid? TerminalId,
    string Label,
    IReadOnlyList<string> SiteIds,
    string QrPayload,
    string ManualCode,
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