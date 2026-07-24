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

/// <summary>Visite telle qu'affichée dans la liste du portail hôte.</summary>
public sealed record HostVisitDto(
    Guid VisitId,
    string VisitorName,
    string VisitorCompany,
    string Mode,
    string Status,
    DateTimeOffset? ScheduledAt,
    bool IsOnSite,
    DateTimeOffset CreatedAt);
