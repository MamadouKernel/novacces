using Microsoft.Extensions.Logging;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Enums;

namespace NovAcces.Application.Visits;

public sealed record DenyConfirmationRequestCommand(Guid RequestId, string DecidedBy);

/// <summary>Refus explicite de la sûreté — aucun scan n'est tenté (contrairement à l'approbation, voir ApproveConfirmationRequestCommand).</summary>
public sealed class DenyConfirmationRequestHandler
{
    private readonly IScanConfirmationRequestRepository _requests;
    private readonly IScanEventBroadcaster _broadcaster;
    private readonly IConfirmationNotifier _notifier;
    private readonly IAdminAuditLog _audit;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<DenyConfirmationRequestHandler> _logger;

    public DenyConfirmationRequestHandler(
        IScanConfirmationRequestRepository requests,
        IScanEventBroadcaster broadcaster,
        IConfirmationNotifier notifier,
        IAdminAuditLog audit,
        IDateTimeProvider clock,
        ILogger<DenyConfirmationRequestHandler> logger)
    {
        _requests = requests;
        _broadcaster = broadcaster;
        _notifier = notifier;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    public async Task<(bool Success, string? Error)> HandleAsync(DenyConfirmationRequestCommand command, CancellationToken ct)
    {
        var now = _clock.UtcNow;

        var request = await _requests.GetByIdAsync(command.RequestId, ct);
        if (request is null)
            return (false, "Demande introuvable.");

        if (!request.IsPending(now))
        {
            return (false, request.Status == ConfirmationRequestStatus.Pending
                ? "Cette demande a expiré."
                : "Cette demande a déjà été traitée.");
        }

        request.Deny(command.DecidedBy, now);
        await _requests.SaveChangesAsync(ct);

        try
        {
            await _audit.RecordAsync(
                AdminAuditAction.ConfirmationRequestDenied, command.DecidedBy, request.Id.ToString(),
                $"Confirmation refusée pour « {request.VisitorName} » ({request.Direction}), demandée par l'agent {request.AgentId}.",
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec de l'écriture au journal d'audit pour la demande {RequestId}.", request.Id);
        }

        try { await _broadcaster.BroadcastConfirmationResolvedAsync(request.Id, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Échec de diffusion de la résolution de la demande {RequestId}.", request.Id); }

        try { await _notifier.NotifyResolvedAsync(request.RequestingTerminalId, granted: false, request.VisitorName, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Échec de la notification de refus au terminal demandeur ({RequestId}).", request.Id); }

        return (true, null);
    }
}
