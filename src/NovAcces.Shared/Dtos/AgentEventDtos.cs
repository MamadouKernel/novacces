namespace NovAcces.Shared.Dtos;

/// <summary>Événement minimal consommé par le cache local de l'app agent.</summary>
public sealed record AgentVisitEventDto(
    Guid VisitId,
    string? VisitorName,
    DateTimeOffset OccurredAt);
