using NovAcces.Domain.Enums;

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

    /// <summary>Une nouvelle demande de confirmation attend la sûreté du site (tap agent sur « Attendus »).</summary>
    Task BroadcastConfirmationRequestedAsync(ConfirmationRequestedEvent requested, CancellationToken ct);

    /// <summary>
    /// Une demande vient d'être tranchée (approuvée/refusée/expirée) — permet à
    /// TOUTE session sûreté connectée (pas seulement celle qui a décidé) de la
    /// retirer instantanément de sa liste « en attente ».
    /// </summary>
    Task BroadcastConfirmationResolvedAsync(Guid requestId, CancellationToken ct);
}

public sealed record ConfirmationRequestedEvent(
    Guid RequestId, string VisitorName, string? CheckpointId, CheckpointDirection Direction, DateTimeOffset ExpiresAt);

public sealed record OverstayBroadcastEvent(
    Guid VisitId,
    string VisitorName,
    int OverstayMinutes,
    int Level,
    bool IsSecurityEvent,
    DateTimeOffset OccurredAt,
    // Permet à ScanEventBroadcaster de cibler AUSSI le groupe SignalR personnel
    // de l'hôte (host:{HostUserId}), en plus du groupe du site (Sûreté) — un
    // hôte doit être alerté en direct du dépassement de SON visiteur, sans
    // jamais recevoir le flux complet du site (moindre privilège).
    string HostUserId);

public sealed record ScanBroadcastEvent(
    Guid VisitId,
    string VisitorName,
    string VerdictCode,
    bool IsGranted,
    bool IsCheckOut,
    bool IsSecurityEvent,
    string AgentId,
    DateTimeOffset OccurredAt);
