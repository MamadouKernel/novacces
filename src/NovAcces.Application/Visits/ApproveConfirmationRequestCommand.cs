using Microsoft.Extensions.Logging;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;

namespace NovAcces.Application.Visits;

public sealed record ApproveConfirmationRequestCommand(Guid RequestId, string DecidedBy);

/// <summary>
/// Décision de la SÛRETÉ (portail Web). Deux écritures distinctes et dans CET
/// ORDRE :
///   1. la demande passe à Approved (c'est un fait : la sûreté a validé) ;
///   2. le scan réel est rejoué en entier via ScanExecutionCore — anti-rejeu,
///      liste d'exclusion relue EN DIRECT, fenêtre de validité, cycle
///      entrée/sortie — jamais court-circuité par l'approbation sûreté.
/// Un « Approved » (1) peut donc coexister avec un refus RÉEL (2) si l'état a
/// changé entre la demande et l'approbation (ex. exclusion ajoutée) — c'est le
/// comportement voulu : la demande enregistre la DÉCISION HUMAINE, le journal
/// des scans enregistre l'ISSUE RÉELLE, ce ne sont pas la même chose.
/// </summary>
public sealed class ApproveConfirmationRequestHandler
{
    private readonly IScanConfirmationRequestRepository _requests;
    private readonly IVisitRepository _visits;
    private readonly IScanLogRepository _logs;
    private readonly IScanEventBroadcaster _broadcaster;
    private readonly IExclusionListService _exclusionList;
    private readonly IHostDirectory _hosts;
    private readonly INotificationService _notifications;
    private readonly IConfirmationNotifier _confirmationNotifier;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IBusinessDayService _businessDays;
    private readonly IAdminAuditLog _audit;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<ApproveConfirmationRequestHandler> _logger;
    private readonly ScanExecutionCore _core;

    public ApproveConfirmationRequestHandler(
        IScanConfirmationRequestRepository requests,
        IVisitRepository visits,
        IScanLogRepository logs,
        IScanEventBroadcaster broadcaster,
        IExclusionListService exclusionList,
        IHostDirectory hosts,
        INotificationService notifications,
        IConfirmationNotifier confirmationNotifier,
        IUnitOfWork unitOfWork,
        IBusinessDayService businessDays,
        IAdminAuditLog audit,
        IDateTimeProvider clock,
        ILogger<ApproveConfirmationRequestHandler> logger)
    {
        _requests = requests;
        _visits = visits;
        _logs = logs;
        _broadcaster = broadcaster;
        _exclusionList = exclusionList;
        _hosts = hosts;
        _notifications = notifications;
        _confirmationNotifier = confirmationNotifier;
        _unitOfWork = unitOfWork;
        _businessDays = businessDays;
        _audit = audit;
        _clock = clock;
        _logger = logger;
        _core = new ScanExecutionCore(visits, logs, broadcaster, exclusionList, hosts, notifications, unitOfWork, clock, logger);
    }

    public async Task<(bool Success, ScanQrResult? Result, string? Error)> HandleAsync(
        ApproveConfirmationRequestCommand command, CancellationToken ct)
    {
        var now = _clock.UtcNow;

        var request = await _requests.GetByIdAsync(command.RequestId, ct);
        if (request is null)
            return (false, null, "Demande introuvable.");

        if (!request.IsPending(now))
        {
            return (false, null, request.Status == ConfirmationRequestStatus.Pending
                ? "Cette demande a expiré — sans réponse sous 15 minutes, elle équivaut à un refus."
                : "Cette demande a déjà été traitée.");
        }

        request.Approve(command.DecidedBy, now);
        await _requests.SaveChangesAsync(ct);

        try
        {
            await _broadcaster.BroadcastConfirmationResolvedAsync(request.Id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec de diffusion de la résolution de la demande {RequestId}.", request.Id);
        }

        var isBusinessDay = _businessDays.IsBusinessDay(now);
        var result = await _core.ExecuteAsync(
            token => _visits.GetForUpdateByIdAsync(request.VisitId, token),
            ScanAuthMethod.SureteConfirmation, ScanDenialReason.VisitNotFound, "VISIT_NOT_FOUND",
            "Visite introuvable", "La visite associée à la demande de confirmation a disparu",
            request.Direction, request.AgentId, isDegradedMode: false, isBusinessDay,
            request.CheckpointId, now, ct);

        // Trace de bout en bout de la décision sûreté (§8.5) — demandeur ET
        // décideur dans la MÊME entrée, avec le verdict RÉEL (qui peut différer
        // de « approuvé », voir le commentaire de classe). scan_logs journalise
        // déjà le scan lui-même, mais sans l'identité du sûreté qui a autorisé
        // l'entrée sans QR — c'est précisément ce que cette entrée comble.
        try
        {
            await _audit.RecordAsync(
                AdminAuditAction.ConfirmationRequestApproved, command.DecidedBy, request.Id.ToString(),
                $"Confirmation approuvée pour « {request.VisitorName} » ({request.Direction}), demandée par l'agent {request.AgentId} — verdict réel : {result.VerdictCode}.",
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec de l'écriture au journal d'audit pour la demande {RequestId}.", request.Id);
        }

        try
        {
            await _confirmationNotifier.NotifyResolvedAsync(request.RequestingTerminalId, result.IsGranted, request.VisitorName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec de la notification de résolution au terminal demandeur ({RequestId}).", request.Id);
        }

        return (true, result, null);
    }
}
