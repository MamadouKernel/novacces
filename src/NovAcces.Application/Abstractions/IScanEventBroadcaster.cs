namespace NovAcces.Application.Abstractions;

/// <summary>
/// Diffusion temps réel d'un scan aux clients connectés du tenant courant
/// (dashboard sûreté, portail hôte — REQ-F-06). Best-effort : l'absence de
/// client connecté ou une panne de diffusion ne doit jamais affecter le
/// résultat du scan lui-même.
/// </summary>
public interface IScanEventBroadcaster
{
    Task BroadcastAsync(ScanBroadcastEvent scanEvent, CancellationToken ct);
}

public sealed record ScanBroadcastEvent(
    Guid VisitId,
    string VisitorName,
    string VerdictCode,
    bool IsGranted,
    bool IsCheckOut,
    bool IsSecurityEvent,
    string AgentId,
    DateTimeOffset OccurredAt);
