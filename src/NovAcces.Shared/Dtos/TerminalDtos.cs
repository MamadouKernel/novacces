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
public sealed record TerminalSummaryDto(
    Guid Id, string Label, IReadOnlyList<string> SiteIds, bool IsActive, DateTimeOffset CreatedAt, bool IsEnrolled = false);

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
public sealed record DeviceEnrollmentRequestDto(
    string Ticket,
    string DeviceInstanceId,
    string DevicePublicKeyPem);

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