namespace NovAcces.Shared.Dtos;

public sealed record ScanRequestDto(string SignedQrPayload, string Direction, string AgentId, string? CheckpointId = null);

public sealed record ScanResponseDto(
    bool IsGranted,
    bool IsCheckOut,
    bool IsSecurityEvent,
    string VerdictCode,
    string? VisitorName,
    int? OverstayMinutes,
    int? PresenceMinutes = null); // durée de présence à la sortie (§1.6)
