namespace NovAcces.Application.Abstractions;

/// <summary>
/// Envoi du QR signé au visiteur (REQ-F-03). WhatsApp Business Platform en
/// canal principal, email en repli automatique (accord-commercial.md — SMS
/// abandonné). Une panne du canal de notification ne doit jamais empêcher
/// la création de la visite : le QR signé reste valide même si son envoi
/// échoue, à charge pour l'hôte de le retransmettre manuellement.
/// </summary>
public interface INotificationService
{
    Task SendVisitInvitationAsync(VisitInvitationNotification notification, CancellationToken ct);
}

public sealed record VisitInvitationNotification(
    Guid VisitId,
    string VisitorName,
    string? VisitorPhone,
    string? VisitorEmail,
    string SignedQrPayload,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset ExpiresAt);
