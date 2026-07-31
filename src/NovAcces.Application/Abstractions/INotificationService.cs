namespace NovAcces.Application.Abstractions;

/// <summary>
/// Envoi du QR signé au visiteur (REQ-F-03) sur les deux canaux convenus :
/// WhatsApp Business Platform et email. Les deux tentatives sont indépendantes
/// : une panne d'un canal ne doit jamais empêcher l'autre tentative ni la
/// création de la visite. Le QR signé reste valide même si les deux envois
/// échouent, à charge pour l'hôte de le retransmettre manuellement.
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
