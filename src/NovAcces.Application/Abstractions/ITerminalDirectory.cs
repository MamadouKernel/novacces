namespace NovAcces.Application.Abstractions;

/// <summary>Terminal identifié par sa clé API, avec les sites qu'il est autorisé à servir.</summary>
public sealed record TerminalIdentity(Guid Id, string Label, IReadOnlyList<string> SiteIds);

/// <summary>Projection d'un terminal pour la console Admin — jamais la clé ni son empreinte.</summary>
public sealed record TerminalSummary(
    Guid Id, string Label, IReadOnlyList<string> SiteIds, bool IsActive, DateTimeOffset CreatedAt, bool IsEnrolled = false);

/// <summary>Terminal supprimé (archivé), pour la consultation en lecture seule.</summary>
public sealed record ArchivedTerminalSummary(
    Guid Id, string Label, IReadOnlyList<string> SiteIds, DateTimeOffset DeletedAt, string? DeletedBy);

/// <summary>Ticket brut remis une seule fois à la console d'administration.</summary>
public sealed record TerminalEnrollmentTicket(
    Guid TerminalId,
    string Label,
    IReadOnlyList<string> SiteIds,
    string Ticket,
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

    /// <summary>Crée un ticket d'enrôlement temporaire, à usage unique.</summary>
    Task<TerminalEnrollmentTicket?> CreateEnrollmentTicketAsync(
        Guid terminalId, string createdBy, TimeSpan lifetime, CancellationToken ct);

    /// <summary>
    /// Consomme le ticket et lie le device. Une nouvelle clé API est générée et
    /// remise une seule fois au mobile.
    /// </summary>
    Task<TerminalActivation?> ActivateAsync(
        string ticket, string deviceInstanceId, string devicePublicKeyPem, CancellationToken ct);

    /// <summary>Enregistre ce jeton de poste comme actif pour ce terminal (remplace le précédent).</summary>
    Task SetActiveShiftAsync(Guid terminalId, string shiftJti, string matricule, DateTimeOffset now, CancellationToken ct);

    /// <summary>Clôt le poste, uniquement s'il correspond au jeton présenté. Idempotent, no-op sinon.</summary>
    Task EndActiveShiftAsync(Guid terminalId, string shiftJti, DateTimeOffset now, CancellationToken ct);

    /// <summary>Le jeton de poste présenté est-il toujours celui en cours pour ce terminal ?</summary>
    Task<bool> IsShiftActiveAsync(Guid terminalId, string shiftJti, CancellationToken ct);
}