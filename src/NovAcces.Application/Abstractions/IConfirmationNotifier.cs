using NovAcces.Domain.Enums;

namespace NovAcces.Application.Abstractions;

/// <summary>
/// Notifie la sûreté d'une demande de confirmation (push web + email) et
/// notifie le terminal demandeur de l'issue réelle (push Expo ciblé, PAS
/// diffusé à tout le site — voir OverstayPushNotifier pour le pattern
/// équivalent des dépassements). Best-effort intégral, comme tout le reste
/// de la couche notification (voir INotificationService).
/// </summary>
public interface IConfirmationNotifier
{
    Task NotifyRequestedAsync(
        string siteId, string visitorName, string? checkpointId, CheckpointDirection direction,
        DateTimeOffset requestedAt, CancellationToken ct);

    /// <summary>
    /// granted reflète le VERDICT RÉEL du scan (post-ScanExecutionCore), pas
    /// seulement la décision sûreté — un « Approved » peut encore aboutir à un
    /// refus (exclusion ajoutée entre-temps, anti-doublon…).
    /// </summary>
    Task NotifyResolvedAsync(Guid requestingTerminalId, bool granted, string visitorName, CancellationToken ct);
}
