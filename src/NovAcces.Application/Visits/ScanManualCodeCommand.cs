using Microsoft.Extensions.Logging;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;

namespace NovAcces.Application.Visits;

public sealed record ScanManualCodeCommand(
    string Code,
    CheckpointDirection Direction,
    string AgentId,
    bool IsDegradedMode,
    bool IsBusinessDayOverride,
    string? CheckpointId = null
);

/// <summary>
/// Cas d'usage transactionnel du scan PAR CODE DE SECOURS — alternative
/// manuelle au QR (visiteur sans téléphone/QR fonctionnel). Résout la visite
/// par l'empreinte du code saisi, puis délègue à ScanExecutionCore, PARTAGÉ
/// avec ScanQrHandler : mêmes règles de sûreté (anti-rejeu, liste
/// d'exclusion relue en direct, fenêtre de validité, cycle entrée/sortie),
/// jamais une logique parallèle. Contrairement au QR, il n'y a pas de
/// vérification cryptographique hors base possible : résoudre le code EST
/// la recherche en base (voir IManualCodeService).
/// </summary>
public sealed class ScanManualCodeHandler
{
    private readonly IManualCodeService _manualCode;
    private readonly IVisitRepository _visits;
    private readonly IDateTimeProvider _clock;
    private readonly ScanExecutionCore _core;

    public ScanManualCodeHandler(
        IManualCodeService manualCode,
        IVisitRepository visits,
        IScanLogRepository logs,
        IDateTimeProvider clock,
        IScanEventBroadcaster broadcaster,
        IExclusionListService exclusionList,
        IHostDirectory hosts,
        INotificationService notifications,
        IUnitOfWork unitOfWork,
        ILogger<ScanManualCodeHandler> logger)
    {
        _manualCode = manualCode;
        _visits = visits;
        _clock = clock;
        _core = new ScanExecutionCore(
            visits, logs, broadcaster, exclusionList, hosts, notifications, unitOfWork, clock, logger);
    }

    public async Task<ScanQrResult> HandleAsync(ScanManualCodeCommand command, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var hash = _manualCode.ComputeHash(command.Code);

        return await _core.ExecuteAsync(
            token => _visits.GetForUpdateByManualCodeHashAsync(hash, token),
            ScanAuthMethod.ManualCode, ScanDenialReason.InvalidManualCode, "INVALID_CODE",
            "Code inconnu", "Code de secours introuvable ou mal saisi",
            command.Direction, command.AgentId, command.IsDegradedMode, command.IsBusinessDayOverride,
            command.CheckpointId, now, ct);
    }
}
