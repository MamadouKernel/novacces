using Microsoft.Extensions.Logging;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;

namespace NovAcces.Application.Visits;

public sealed record CreateVisitCommand(
    string VisitorName,
    string VisitorCompany,
    string Motif,
    string HostUserId,
    AccessMode Mode,
    DateTimeOffset? ScheduledAt,
    int PlannedDurationMinutes,
    string? VisitorPhone,
    string? VisitorEmail
);

public sealed record CreateVisitResult(Guid VisitId, string SignedQrPayload, DateTimeOffset ExpiresAt);

/// <summary>
/// Création d'une demande de visite et génération du QR signé.
/// Garde-fou anti-doublon appliqué en amont par l'appelant (Api) via une
/// recherche des visites actives du même visiteur — cf. maquette de démo.
/// </summary>
public sealed class CreateVisitHandler
{
    private readonly IVisitRepository _visits;
    private readonly IQrSigningService _signing;
    private readonly IDateTimeProvider _clock;
    private readonly IExclusionListService _exclusionList;
    private readonly INotificationService _notifications;
    private readonly ILogger<CreateVisitHandler> _logger;

    public CreateVisitHandler(
        IVisitRepository visits,
        IQrSigningService signing,
        IDateTimeProvider clock,
        IExclusionListService exclusionList,
        INotificationService notifications,
        ILogger<CreateVisitHandler> logger)
    {
        _visits = visits;
        _signing = signing;
        _clock = clock;
        _exclusionList = exclusionList;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<CreateVisitResult> HandleAsync(CreateVisitCommand command, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var isExcluded = await _exclusionList.IsExcludedAsync(command.VisitorName, ct);

        var visit = Visit.Create(
            command.VisitorName, command.VisitorCompany, command.Motif, command.HostUserId,
            command.Mode, command.ScheduledAt, command.PlannedDurationMinutes,
            command.VisitorPhone, command.VisitorEmail, isExcluded, now);

        // Calcul délégué au domaine (Visit.ComputeQrExpiry) : c'est la MÊME
        // formule que réutilisera GET /api/visits/{id}/qr pour réémettre le
        // badge d'une demande existante — une divergence produirait un second
        // QR d'expiration différente pour une même visite.
        var expiresAt = visit.ComputeQrExpiry();

        var signedPayload = _signing.SignVisitToken(visit.Id, visit.VisitToken, expiresAt);

        await _visits.AddAsync(visit, ct);
        await _visits.SaveChangesAsync(ct);

        // REQ-F-03 : envoi automatique du QR au visiteur. Best-effort — un
        // échec de notification ne doit jamais invalider une visite dont le
        // QR est déjà signé et enregistré.
        try
        {
            await _notifications.SendVisitInvitationAsync(
                new VisitInvitationNotification(
                    visit.Id, visit.VisitorName, visit.VisitorPhone, visit.VisitorEmail,
                    signedPayload, visit.ScheduledAt, expiresAt),
                ct);
        }
        catch (Exception ex)
        {
            // Minimisation des données : on journalise l'identifiant opaque de
            // la visite (corrélable au journal, sous contrôle d'accès), pas le
            // nom du visiteur (PII).
            _logger.LogWarning(ex,
                "Échec de l'envoi de l'invitation pour la visite {VisitId} — le QR reste valide.",
                visit.Id);
        }

        return new CreateVisitResult(visit.Id, signedPayload, expiresAt);
    }
}

/// <summary>
/// Liste d'exclusion du site (REQ-F-11) — implémentation Infrastructure.
/// La vérification (IsExcludedAsync) est utilisée à la création d'une visite ;
/// la gestion (liste/ajout/retrait) est réservée à la sûreté (moindre privilège).
/// </summary>
public interface IExclusionListService
{
    Task<bool> IsExcludedAsync(string visitorName, CancellationToken ct);

    /// <summary>
    /// Noms NORMALISÉS de toutes les personnes exclues du site. Sert à marquer
    /// la liste hors-ligne signée sans une requête par visiteur : le mode
    /// dégradé doit refuser une personne écartée après l'émission de son QR,
    /// exactement comme le scan en ligne. Ne contient aucun motif (moindre
    /// privilège : l'agent ne doit jamais voir pourquoi).
    /// </summary>
    Task<IReadOnlySet<string>> GetExcludedNormalizedNamesAsync(CancellationToken ct);

    Task<IReadOnlyList<ExclusionEntryView>> ListAsync(CancellationToken ct);

    /// <summary>Ajoute (ou retrouve, si déjà présent) une entrée et retourne son identifiant.</summary>
    Task<Guid> AddAsync(string displayName, string reason, string addedBy, CancellationToken ct);

    /// <summary>
    /// Retire une entrée et renvoie ce qu'elle contenait (null si introuvable).
    /// L'appelant DOIT inscrire ce contenu au journal d'audit : la ligne étant
    /// physiquement supprimée, c'est la dernière occasion de conserver une trace
    /// de QUI a été retiré de la liste et pour quel motif il y figurait — sans
    /// quoi le journal ne référencerait qu'un identifiant devenu orphelin.
    /// </summary>
    Task<ExclusionEntryView?> RemoveAsync(Guid id, CancellationToken ct);
}

/// <summary>Projection d'une entrée d'exclusion pour la sûreté (motif inclus).</summary>
public sealed record ExclusionEntryView(
    Guid Id, string DisplayName, string Reason, string AddedBy, DateTimeOffset CreatedAt);
