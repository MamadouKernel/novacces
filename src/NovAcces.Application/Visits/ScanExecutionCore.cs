using Microsoft.Extensions.Logging;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;

namespace NovAcces.Application.Visits;

/// <summary>
/// Cœur transactionnel PARTAGÉ par tous les moyens d'authentification d'un
/// scan (QR aujourd'hui, code de secours — voir ScanManualCodeCommand).
/// Existe pour que l'anti-rejeu (REQ-SEC-03) ne soit écrit et testé qu'UNE
/// SEULE FOIS : chaque handler ne fait que résoudre la visite (comment ?)
/// avant de déléguer ici (que faire une fois résolue ? — identique pour
/// tous). Ne JAMAIS dupliquer ce bloc dans un nouveau handler de scan.
/// </summary>
internal sealed class ScanExecutionCore
{
    private readonly IVisitRepository _visits;
    private readonly IScanLogRepository _logs;
    private readonly IScanEventBroadcaster _broadcaster;
    private readonly IExclusionListService _exclusionList;
    private readonly IHostDirectory _hosts;
    private readonly INotificationService _notifications;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger _logger;

    public ScanExecutionCore(
        IVisitRepository visits,
        IScanLogRepository logs,
        IScanEventBroadcaster broadcaster,
        IExclusionListService exclusionList,
        IHostDirectory hosts,
        INotificationService notifications,
        IUnitOfWork unitOfWork,
        IDateTimeProvider clock,
        ILogger logger)
    {
        _visits = visits;
        _logs = logs;
        _broadcaster = broadcaster;
        _exclusionList = exclusionList;
        _hosts = hosts;
        _notifications = notifications;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ScanQrResult> ExecuteAsync(
        Func<CancellationToken, Task<Visit?>> loadVisitForUpdate,
        ScanAuthMethod authMethod,
        ScanDenialReason notFoundReason,
        string notFoundVerdictCode,
        string notFoundLabel,
        string notFoundDetail,
        CheckpointDirection direction,
        string agentId,
        bool isDegradedMode,
        bool isBusinessDayOverride,
        string? checkpointId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // Section critique de l'anti-rejeu (REQ-SEC-03) enveloppée dans UNE
        // transaction : le verrou pessimiste posé par loadVisitForUpdate
        // (SELECT … FOR UPDATE) doit être tenu jusqu'à la sauvegarde incluse,
        // sinon deux scans simultanés de la même visite pourraient passer
        // tous les deux. La diffusion temps réel se fait APRÈS le commit
        // (voir plus bas) pour ne jamais annoncer un scan qui aurait été
        // annulé (rollback).
        ScanBroadcastEvent? broadcast = null;
        (string HostUserId, HostEventKind Kind, Guid VisitId, string VisitorName,
            int PresenceMinutes, int OverstayMinutes)? hostEvent = null;

        var result = await _unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            // Chargement AVEC VERROU PESSIMISTE, tenu pour toute la transaction.
            var visit = await loadVisitForUpdate(token);
            if (visit is null)
            {
                var missingEntry = ScanLogEntry.Create(
                    Guid.Empty, notFoundLabel, agentId, direction,
                    ScanOutcome.Denied(notFoundReason, isSecurityEvent: true),
                    isDegradedMode, notFoundDetail, now, authMethod, checkpointId);
                await _logs.AddAsync(missingEntry, token);
                await _logs.SaveChangesAsync(token);
                return new ScanQrResult(false, false, true, notFoundVerdictCode, null, null);
            }

            // Liste d'exclusion RELUE MAINTENANT, dans la transaction (REQ-F-11).
            // Le booléen figé sur la visite ne reflète que l'état à sa
            // création : une personne écartée après l'émission de son
            // QR/code doit être refusée au poste, sans qu'on ait à révoquer
            // sa demande à la main.
            var isOnExclusionList = await _exclusionList.IsExcludedAsync(visit.VisitorName, token);

            // Application de la règle métier (Domain) — jamais dupliquée ici.
            var outcome = visit.Scan(direction, isBusinessDayOverride, now, isOnExclusionList);

            // Journalisation inaltérable, y compris pour les refus. Le dépôt
            // de scans partage le même DbContext : un seul SaveChanges
            // persiste atomiquement la mutation de la visite ET l'entrée de
            // journal.
            var logEntry = ScanLogEntry.Create(
                visit.Id, visit.VisitorName, agentId, direction,
                outcome, isDegradedMode, BuildDetail(outcome), now, authMethod, checkpointId);

            await _logs.AddAsync(logEntry, token);
            await _visits.SaveChangesAsync(token);

            var verdictCode = outcome switch
            {
                { IsGranted: true, IsCheckOut: true } => "CHECKED_OUT",
                { IsGranted: true } => "GRANTED",
                _ => $"DENIED_{outcome.DenialReason}"
            };

            broadcast = new ScanBroadcastEvent(
                visit.Id, visit.VisitorName, verdictCode,
                outcome.IsGranted, outcome.IsCheckOut, outcome.IsSecurityEvent,
                agentId, now);

            // Événement à remonter à l'HÔTE (§1.3, §1.6, §2). Capturé DANS la
            // transaction (on a l'entité sous la main) mais envoyé APRÈS le
            // commit : on ne prévient jamais d'une arrivée qui pourrait
            // encore être annulée par un rollback.
            hostEvent = outcome switch
            {
                { IsCheckOut: true } => (visit.HostUserId, HostEventKind.Departure, visit.Id, visit.VisitorName,
                    outcome.PresenceMinutesAtCheckOut, outcome.OverstayMinutesAtCheckOut),
                { IsGranted: true } => (visit.HostUserId, HostEventKind.Arrival, visit.Id, visit.VisitorName, 0, 0),
                { DenialReason: ScanDenialReason.SuspectedDuplicate } => (visit.HostUserId,
                    HostEventKind.SuspectedDuplicate, visit.Id, visit.VisitorName, 0, 0),
                _ => null,
            };

            return new ScanQrResult(
                outcome.IsGranted, outcome.IsCheckOut, outcome.IsSecurityEvent,
                verdictCode, visit.VisitorName,
                outcome.IsCheckOut ? outcome.OverstayMinutesAtCheckOut : null,
                outcome.IsCheckOut ? outcome.PresenceMinutesAtCheckOut : null,
                visit.Id);
        }, ct);

        // REQ-F-06 : diffusion temps réel (dashboard sûreté / portail hôte),
        // une fois le scan COMMITÉ. Best-effort — une panne de diffusion ne
        // doit jamais invalider un scan déjà journalisé.
        if (broadcast is not null)
        {
            try
            {
                await _broadcaster.BroadcastAsync(broadcast, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Échec de diffusion temps réel du scan pour la visite {VisitId}.", broadcast.VisitId);
            }
        }

        // §1.3 / §1.6 / §2 : l'hôte est prévenu de l'arrivée, du départ, ou
        // d'une présentation anormale de SON visiteur. Comme la diffusion :
        // après commit, best-effort, jamais bloquant pour le poste de contrôle.
        if (hostEvent is { } evt)
            await NotifyHostAsync(evt, ct);

        return result;
    }

    private async Task NotifyHostAsync(
        (string HostUserId, HostEventKind Kind, Guid VisitId, string VisitorName,
            int PresenceMinutes, int OverstayMinutes) evt,
        CancellationToken ct)
    {
        try
        {
            var host = await _hosts.FindAsync(evt.HostUserId, ct);
            if (host is null) return; // hôte introuvable ou désactivé : rien à faire

            await _notifications.NotifyHostAsync(new HostEventNotification(
                evt.Kind, evt.VisitId, evt.VisitorName, host, _clock.UtcNow,
                PresenceMinutes: evt.Kind == HostEventKind.Departure ? evt.PresenceMinutes : null,
                OverstayMinutes: evt.Kind == HostEventKind.Departure ? evt.OverstayMinutes : null), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Échec de la notification de l'hôte pour la visite {VisitId}.", evt.VisitId);
        }
    }

    internal static string BuildDetail(ScanOutcome outcome) => outcome switch
    {
        { IsCheckOut: true, OverstayMinutesAtCheckOut: > 0 } o =>
            $"Sortie enregistrée · dépassement de la durée prévue : +{o.OverstayMinutesAtCheckOut} min",
        { IsCheckOut: true } => "Sortie enregistrée",
        { IsGranted: true } => "Accès autorisé",
        { DenialReason: ScanDenialReason.Excluded } => "Personne figurant sur la liste d'exclusion du site (REQ-F-11)",
        { DenialReason: ScanDenialReason.SuspectedDuplicate } => "QR présenté à l'entrée alors qu'une entrée est déjà enregistrée — suspicion de copie",
        { DenialReason: ScanDenialReason.CycleAlreadyClosed } => "Réutilisation d'un QR unique après sortie du visiteur",
        { DenialReason: ScanDenialReason.AlreadyConsumed } => "Tentative de réutilisation d'un QR à passage unique (anti-rejeu)",
        { DenialReason: ScanDenialReason.TooEarly } => "Présentation avant l'ouverture de la fenêtre de validité",
        { DenialReason: ScanDenialReason.TooLate } => "Tentative hors fenêtre de validité",
        { DenialReason: ScanDenialReason.NonBusinessDay } => "Accès 30 jours présenté un jour non ouvré",
        { DenialReason: ScanDenialReason.Revoked } => "Scan d'un QR révoqué",
        { DenialReason: ScanDenialReason.NoActiveEntry } => "Scan au poste de sortie sans entrée enregistrée",
        { DenialReason: ScanDenialReason.VisitNotFound } => "Visite introuvable au moment de l'approbation sûreté",
        _ => "Refus"
    };
}
