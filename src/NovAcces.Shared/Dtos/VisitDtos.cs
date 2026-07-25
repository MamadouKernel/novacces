namespace NovAcces.Shared.Dtos;

public sealed record CreateVisitRequestDto(
    string VisitorName,
    string VisitorCompany,
    string Motif,
    string Mode,          // "Unique" | "ThirtyDays"
    DateTimeOffset? ScheduledAt,
    int PlannedDurationMinutes,
    string? VisitorPhone,
    string? VisitorEmail);

public sealed record CreateVisitResponseDto(Guid VisitId, string SignedQrPayload, DateTimeOffset ExpiresAt);

// ---- Création groupée (invitation d'un groupe de visiteurs en une fois) ----

/// <summary>Lot de demandes à créer en une seule opération.</summary>
public sealed record BulkCreateVisitsRequestDto(IReadOnlyList<CreateVisitRequestDto> Visits);

/// <summary>Résultat d'une ligne du lot : QR généré, ou motif d'échec.</summary>
public sealed record BulkCreateVisitItemDto(
    string VisitorName,
    bool Success,
    Guid? VisitId,
    string? SignedQrPayload,
    DateTimeOffset? ExpiresAt,
    string? Error);

/// <summary>Synthèse d'une création groupée.</summary>
public sealed record BulkCreateVisitsResponseDto(
    int Created,
    int Failed,
    IReadOnlyList<BulkCreateVisitItemDto> Items);

/// <summary>Visite telle qu'affichée dans la liste du portail hôte.</summary>
public sealed record HostVisitDto(
    Guid VisitId,
    string VisitorName,
    string VisitorCompany,
    string Motif,
    string Mode,
    string Status,
    DateTimeOffset? ScheduledAt,
    int PlannedDurationMinutes,
    bool IsOnSite,
    DateTimeOffset CreatedAt);

/// <summary>Visiteur connu pour l'autocomplétion (pré-remplissage).</summary>
public sealed record KnownVisitorDto(
    string Name, string Company, string Motif, int PlannedDurationMinutes);
