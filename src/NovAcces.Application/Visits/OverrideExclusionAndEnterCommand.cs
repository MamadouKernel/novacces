using Microsoft.Extensions.Logging;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;

namespace NovAcces.Application.Visits;

public sealed record OverrideExclusionAndEnterCommand(Guid VisitId, string DecidedBy, string Justification);

/// <summary>
/// Contournement EXPLICITE d'une correspondance sur la liste d'exclusion,
/// réservé à la sûreté (REQ-F-11 : la comparaison ne porte que sur le nom —
/// voir ExclusionListService — donc un homonyme innocent d'une personne
/// réellement exclue serait sinon bloqué sans recours). Toujours une ENTRÉE
/// (l'exclusion ne s'applique jamais à une sortie réelle, voir Visit.Scan) et
/// toujours rejouée en entier via ScanExecutionCore : seule l'exclusion est
/// court-circuitée, l'anti-rejeu, la fenêtre de validité et le cycle
/// entrée/sortie restent appliqués normalement — un contournement d'exclusion
/// n'est PAS un passe-droit sur le reste des règles.
/// </summary>
public sealed class OverrideExclusionAndEnterHandler
{
    private readonly IVisitRepository _visits;
    private readonly IAdminAuditLog _audit;
    private readonly IBusinessDayService _businessDays;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<OverrideExclusionAndEnterHandler> _logger;
    private readonly ScanExecutionCore _core;

    public OverrideExclusionAndEnterHandler(
        IVisitRepository visits,
        IScanLogRepository logs,
        IScanEventBroadcaster broadcaster,
        IExclusionListService exclusionList,
        IHostDirectory hosts,
        INotificationService notifications,
        IUnitOfWork unitOfWork,
        IBusinessDayService businessDays,
        IAdminAuditLog audit,
        IDateTimeProvider clock,
        ILogger<OverrideExclusionAndEnterHandler> logger)
    {
        _visits = visits;
        _audit = audit;
        _businessDays = businessDays;
        _clock = clock;
        _logger = logger;
        _core = new ScanExecutionCore(visits, logs, broadcaster, exclusionList, hosts, notifications, unitOfWork, clock, logger);
    }

    public async Task<(bool Success, ScanQrResult? Result, string? Error)> HandleAsync(
        OverrideExclusionAndEnterCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.Justification))
            return (false, null, "Une justification est requise pour outrepasser une exclusion.");

        var visit = await _visits.GetByIdAsync(command.VisitId, ct);
        if (visit is null)
            return (false, null, "Demande introuvable.");

        var now = _clock.UtcNow;
        var isBusinessDay = _businessDays.IsBusinessDay(now);

        var result = await _core.ExecuteAsync(
            token => _visits.GetForUpdateByIdAsync(command.VisitId, token),
            ScanAuthMethod.ExclusionOverride, ScanDenialReason.VisitNotFound, "VISIT_NOT_FOUND",
            "Visite introuvable", "La visite ciblée par le contournement d'exclusion a disparu",
            CheckpointDirection.Entry, command.DecidedBy, isDegradedMode: false, isBusinessDay,
            checkpointId: null, now, ct, exclusionOverrideBySecurity: true);

        // Audit AVANT tout retour : un contournement d'exclusion sans trace
        // serait la pire régression possible ici (§8.5, moindre privilège).
        // Best-effort volontairement absent — contrairement aux notifications,
        // l'échec de CETTE écriture doit remonter à l'appelant.
        await _audit.RecordAsync(
            AdminAuditAction.ExclusionOverridden, command.DecidedBy, command.VisitId.ToString(),
            $"Entrée de « {visit.VisitorName} » autorisée malgré une correspondance sur la liste d'exclusion — justification : « {command.Justification.Trim()} ». Verdict réel : {result.VerdictCode}.",
            ct);

        return (true, result, null);
    }
}
