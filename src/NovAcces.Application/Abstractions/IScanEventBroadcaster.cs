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

    /// <summary>Diffuse une alerte de dépassement de durée (§7) au dashboard du site.</summary>
    Task BroadcastOverstayAsync(OverstayBroadcastEvent overstay, CancellationToken ct);
}

public sealed record OverstayBroadcastEvent(
    Guid VisitId,
    string VisitorName,
    int OverstayMinutes,
    int Level,
    bool IsSecurityEvent,
    DateTimeOffset OccurredAt);

public sealed record ScanBroadcastEvent(
    Guid VisitId,
    string VisitorName,
    string VerdictCode,
    bool IsGranted,
    bool IsCheckOut,
    bool IsSecurityEvent,
    string AgentId,
    DateTimeOffset OccurredAt);
