namespace NovAcces.Application.Abstractions;

/// <summary>
/// Envoi du QR signé au visiteur (REQ-F-03), par email uniquement (décision
/// M. Kodjo du 01/08/2026 — WhatsApp abandonné, voir docs/accord-commercial.md
/// et docs/scenarios-fonctionnels.md §1). Best-effort : une panne d'envoi ne
/// doit jamais empêcher la création de la visite. Le QR signé reste valide
/// même si l'envoi échoue, à charge pour l'hôte de le retransmettre manuellement.
/// </summary>
public interface INotificationService
{
    Task SendVisitInvitationAsync(VisitInvitationNotification notification, CancellationToken ct);

    /// <summary>
    /// Prévient l'HÔTE d'un événement concernant SON visiteur (§1.3, §1.6, §2,
    /// §7). Best-effort et hors transaction, comme l'invitation : un canal
    /// indisponible ne doit jamais invalider un scan déjà journalisé.
    /// </summary>
    Task NotifyHostAsync(HostEventNotification notification, CancellationToken ct);

    /// <summary>
    /// Envoie le lien de réinitialisation de mot de passe (self-service).
    /// Best-effort : un échec d'envoi ne doit jamais faire fuiter, via une
    /// exception ou un délai différent, l'existence du compte à l'appelant.
    /// </summary>
    Task SendPasswordResetAsync(PasswordResetNotification notification, CancellationToken ct);
}

/// <summary>Nature de l'événement remonté à l'hôte — détermine le message envoyé.</summary>
public enum HostEventKind
{
    /// <summary>Le visiteur vient d'entrer sur le site (§1.3).</summary>
    Arrival,

    /// <summary>Le visiteur vient de quitter le site (§1.6).</summary>
    Departure,

    /// <summary>
    /// Le QR du visiteur a été présenté à l'entrée alors qu'il était déjà
    /// marqué présent : suspicion de copie (§2). L'hôte est prévenu pour qu'il
    /// vérifie si son visiteur est réellement arrivé.
    /// </summary>
    SuspectedDuplicate,

    /// <summary>Le visiteur dépasse la durée de visite prévue (§7).</summary>
    Overstay,
}

public sealed record HostEventNotification(
    HostEventKind Kind,
    Guid VisitId,
    string VisitorName,
    HostContact Host,
    DateTimeOffset OccurredAt,
    int? PresenceMinutes = null,
    int? OverstayMinutes = null,
    int? OverstayLevel = null);

public sealed record VisitInvitationNotification(
    Guid VisitId,
    string VisitorName,
    string? VisitorEmail,
    string SignedQrPayload,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset ExpiresAt,
    string ManualCode);

public sealed record PasswordResetNotification(string Email, string DisplayName, string ResetLink);
