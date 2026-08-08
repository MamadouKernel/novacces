using Microsoft.Extensions.Logging;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;

namespace NovAcces.Application.Visits;

public sealed record CreateConfirmationRequestCommand(
    Guid VisitId, CheckpointDirection Direction, string AgentId, Guid RequestingTerminalId, string? CheckpointId = null);

public sealed record CreateConfirmationRequestResult(Guid RequestId, DateTimeOffset ExpiresAt, bool AlreadyPending);

/// <summary>
/// Ouvre une demande de confirmation sûreté — remplace l'ancien comportement
/// du tap « Attendus » côté agent, qui appelait /api/scan avec un visitId brut
/// (toujours rejeté INVALID_SIGNATURE, Es256QrSigningService ne pouvant pas
/// désérialiser un GUID comme enveloppe signée — bug corrigé le 08/08/2026 par
/// ce vrai flux). Ne fait AUCUNE vérification métier (exclusion, fenêtre,
/// anti-doublon) : c'est le rôle de ScanExecutionCore, exécuté au moment de
/// l'APPROBATION (voir ApproveConfirmationRequestCommand), jamais ici.
/// </summary>
public sealed class CreateConfirmationRequestHandler
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private readonly IVisitRepository _visits;
    private readonly IScanConfirmationRequestRepository _requests;
    private readonly IScanEventBroadcaster _broadcaster;
    private readonly IConfirmationNotifier _notifier;
    private readonly ICurrentTenant _tenant;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<CreateConfirmationRequestHandler> _logger;

    public CreateConfirmationRequestHandler(
        IVisitRepository visits,
        IScanConfirmationRequestRepository requests,
        IScanEventBroadcaster broadcaster,
        IConfirmationNotifier notifier,
        ICurrentTenant tenant,
        IDateTimeProvider clock,
        ILogger<CreateConfirmationRequestHandler> logger)
    {
        _visits = visits;
        _requests = requests;
        _broadcaster = broadcaster;
        _notifier = notifier;
        _tenant = tenant;
        _clock = clock;
        _logger = logger;
    }

    public async Task<(bool Success, CreateConfirmationRequestResult? Result, string? Error)> HandleAsync(
        CreateConfirmationRequestCommand command, CancellationToken ct)
    {
        var now = _clock.UtcNow;

        var visit = await _visits.GetByIdAsync(command.VisitId, ct);
        if (visit is null)
            return (false, null, "Visite introuvable.");

        var existing = await _requests.GetPendingForVisitAsync(command.VisitId, command.Direction, ct);
        if (existing is not null)
            return (true, new CreateConfirmationRequestResult(existing.Id, existing.ExpiresAt, AlreadyPending: true), null);

        var request = ScanConfirmationRequest.Create(
            visit.Id, visit.VisitorName, command.Direction, command.CheckpointId,
            command.AgentId, command.RequestingTerminalId, now, Ttl);

        await _requests.AddAsync(request, ct);
        await _requests.SaveChangesAsync(ct);

        try
        {
            await _broadcaster.BroadcastConfirmationRequestedAsync(new ConfirmationRequestedEvent(
                request.Id, request.VisitorName, request.CheckpointId, request.Direction, request.ExpiresAt), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec de diffusion temps réel de la demande de confirmation {RequestId}.", request.Id);
        }

        try
        {
            await _notifier.NotifyRequestedAsync(
                _tenant.SiteId, request.VisitorName, request.CheckpointId, request.Direction, request.RequestedAt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec de notification (push/email) de la demande de confirmation {RequestId}.", request.Id);
        }

        return (true, new CreateConfirmationRequestResult(request.Id, request.ExpiresAt, AlreadyPending: false), null);
    }
}
