namespace NovAcces.Shared.Dtos;

/// <summary>
/// Ligne « attendus aujourd'hui » pour l'agent (§11). Moindre privilège : nom +
/// statut + fenêtre horaire UNIQUEMENT — jamais motif, entreprise ni coordonnées.
/// </summary>
public sealed record ExpectedVisitorDto(
    string VisitorName,
    string Status,                 // attendu | sur site | sorti | révoqué
    DateTimeOffset? WindowStart,
    DateTimeOffset? WindowEnd);

/// <summary>
/// Liste hors-ligne signée (§6) : le jeton signé à charger dans l'app agent, plus
/// les instants d'émission et d'expiration (TTL) affichés hors ligne.
/// </summary>
public sealed record OfflineListDto(
    string SignedList,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    int EntryCount);

/// <summary>Un scan effectué hors ligne, remonté à la resynchronisation.</summary>
public sealed record OfflineScanDto(
    Guid VisitToken,
    string Direction,              // Entry | Exit
    bool WasGranted,
    DateTimeOffset OccurredAt);

/// <summary>Lot de scans hors-ligne à confronter au registre central.</summary>
public sealed record ResyncRequestDto(IReadOnlyList<OfflineScanDto> Scans);

/// <summary>
/// Résultat de la resynchronisation (§6.5) : nombre de scans confrontés et les
/// conflits détectés (ex. QR révoqué pendant la coupure) — événements de sécurité.
/// </summary>
public sealed record ResyncResultDto(
    int Processed,
    IReadOnlyList<ResyncConflictDto> Conflicts);

public sealed record ResyncConflictDto(
    Guid VisitToken,
    string VisitorName,
    string Reason,
    DateTimeOffset OccurredAt);
