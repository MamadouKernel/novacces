namespace NovAcces.Application.Abstractions;

/// <summary>Terminal identifié par sa clé API, avec les sites qu'il est autorisé à servir.</summary>
public sealed record TerminalIdentity(Guid Id, string Label, IReadOnlyList<string> SiteIds);

/// <summary>Projection d'un terminal pour la console Admin — jamais la clé ni son empreinte.</summary>
/// <param name="PendingTicketExpiresAt">
/// Expiration du dernier ticket d'enrôlement non utilisé/non révoqué de ce
/// terminal (null si aucun ticket n'a jamais été émis). Peut être dans le
/// passé : c'est précisément ce que la console doit distinguer d'un ticket
/// encore valide — bug du 05/08/2026, le statut "En attente" ne changeait
/// jamais après l'expiration du QR d'enrôlement, sans aucun moyen de le voir.
/// </param>
public sealed record TerminalSummary(
    Guid Id, string Label, IReadOnlyList<string> SiteIds, bool IsActive, DateTimeOffset CreatedAt,
    bool IsEnrolled = false, DateTimeOffset? PendingTicketExpiresAt = null,
    string? CheckpointId = null, string? DeviceModel = null);

/// <summary>Terminal supprimé (archivé), pour la consultation en lecture seule.</summary>
public sealed record ArchivedTerminalSummary(
    Guid Id, string Label, IReadOnlyList<string> SiteIds, DateTimeOffset DeletedAt, string? DeletedBy);

/// <summary>
/// Ticket brut remis une seule fois à la console d'administration.
/// <paramref name="ManualCode"/> est une alternative de secours au QR (même
/// ticket, même expiration) pour le cas où la caméra du terminal est hors
/// service. <paramref name="TerminalId"/> est nul pour un ticket de POSTE
/// (voir CreatePosteEnrollmentTicketAsync) : aucun terminal précis n'existe
/// encore avant le premier scan, et contrairement au ticket historique, ce
/// n'est PAS un usage unique — le même QR reste scannable par plusieurs
/// appareils jusqu'à son expiration, chacun créant son propre terminal.
/// </summary>
public sealed record TerminalEnrollmentTicket(
    Guid? TerminalId,
    string Label,
    IReadOnlyList<string> SiteIds,
    string Ticket,
    string ManualCode,
    DateTimeOffset ExpiresAt);

/// <summary>Résultat de l'activation, avec une nouvelle clé API remise au device.</summary>
public sealed record TerminalActivation(
    Guid TerminalId,
    string Label,
    IReadOnlyList<string> SiteIds,
    string ApiKey,
    DateTimeOffset EnrolledAt);

/// <summary>
/// Annuaire des terminaux enrôlés. Les terminaux vivent dans le schéma partagé
/// identity car ils peuvent servir plusieurs sites.
/// </summary>
public interface ITerminalDirectory
{
    Task<TerminalIdentity?> VerifyAsync(string presentedApiKey, CancellationToken ct);

    /// <summary>
    /// Création d'un terminal non enrôlé. Aucun secret n'est retourné.
    /// Le secret opérationnel est généré uniquement lors de l'activation QR.
    /// </summary>
    Task<Guid> CreateAsync(string label, IReadOnlyList<string> siteIds, CancellationToken ct);

    Task<IReadOnlyList<TerminalSummary>> ListAsync(CancellationToken ct);

    Task<bool> RevokeAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Suppression logique (archivage) d'un terminal déjà révoqué. Retourne
    /// false si introuvable (ou déjà supprimé). Lève
    /// <see cref="InvalidOperationException"/> si le terminal est encore actif
    /// (révoquer d'abord).
    /// </summary>
    Task<bool> DeleteAsync(Guid id, string actor, CancellationToken ct);

    /// <summary>Liste les terminaux supprimés (archivés), en lecture seule, pour l'administration.</summary>
    Task<IReadOnlyList<ArchivedTerminalSummary>> ListArchivedAsync(CancellationToken ct);

    /// <summary>Crée un ticket d'enrôlement temporaire pour un terminal PRÉCRÉÉ, à usage unique.</summary>
    Task<TerminalEnrollmentTicket?> CreateEnrollmentTicketAsync(
        Guid terminalId, string createdBy, TimeSpan lifetime, CancellationToken ct);

    /// <summary>
    /// Crée un ticket d'enrôlement de POSTE (09/08/2026) : réutilisable dans sa
    /// fenêtre de validité, sans terminal précréé — chaque scan (voir
    /// ActivateAsync) crée un nouveau Terminal à partir de ce gabarit
    /// (label + sites + poste), permettant d'enrôler N appareils physiques
    /// pour un même poste depuis un seul QR.
    /// </summary>
    Task<TerminalEnrollmentTicket?> CreatePosteEnrollmentTicketAsync(
        string label, IReadOnlyList<string> siteIds, string? checkpointId,
        string createdBy, TimeSpan lifetime, CancellationToken ct);

    /// <summary>
    /// Consomme le ticket et lie le device. Une nouvelle clé API est générée et
    /// remise une seule fois au mobile. Pour un ticket de poste, le ticket
    /// N'EST PAS consommé (reste scannable par d'autres appareils) : c'est un
    /// nouveau Terminal qui est créé et lié à ce device.
    /// </summary>
    Task<TerminalActivation?> ActivateAsync(
        string ticket, string deviceInstanceId, string devicePublicKeyPem, CancellationToken ct);

    /// <summary>Enregistre ce jeton de poste comme actif pour ce terminal (remplace le précédent).</summary>
    Task SetActiveShiftAsync(Guid terminalId, string shiftJti, string matricule, DateTimeOffset now, CancellationToken ct);

    /// <summary>Clôt le poste, uniquement s'il correspond au jeton présenté. Idempotent, no-op sinon.</summary>
    Task EndActiveShiftAsync(Guid terminalId, string shiftJti, DateTimeOffset now, CancellationToken ct);

    /// <summary>Le jeton de poste présenté est-il toujours celui en cours pour ce terminal ?</summary>
    Task<bool> IsShiftActiveAsync(Guid terminalId, string shiftJti, CancellationToken ct);

    /// <summary>
    /// Enregistre (ou efface, si null/vide) le jeton de notification push Expo
    /// de ce terminal — §7, alerte de dépassement même app fermée.
    /// </summary>
    Task SetPushTokenAsync(Guid terminalId, string? expoPushToken, CancellationToken ct);

    /// <summary>Affecte (Admin) ce terminal à un poste — un poste peut regrouper plusieurs terminaux.</summary>
    Task SetCheckpointAsync(Guid terminalId, string? checkpointId, CancellationToken ct);

    /// <summary>Enregistre le modèle/OS de l'appareil (remonté par l'app, purement informatif — voir Terminal.DeviceModel).</summary>
    Task SetDeviceModelAsync(Guid terminalId, string? deviceModel, CancellationToken ct);

    /// <summary>Jeton push Expo d'UN terminal précis (pas tous ceux du site) — cible la notification de résolution d'une demande de confirmation (voir IConfirmationPushNotifier).</summary>
    Task<string?> GetExpoPushTokenAsync(Guid terminalId, CancellationToken ct);
}