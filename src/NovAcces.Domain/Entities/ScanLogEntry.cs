using NovAcces.Domain.Enums;

namespace NovAcces.Domain.Entities;

/// <summary>
/// Journal d'audit inaltérable (REQ-SEC-05, REQ-F-07, section 8.5 du CDC).
/// Écrit uniquement en ajout — jamais de mise à jour ni de suppression
/// depuis l'application (contrainte imposée au niveau des permissions SQL
/// en production, cf. Infrastructure/Persistence).
/// </summary>
public sealed class ScanLogEntry
{
    public Guid Id { get; private set; }
    public Guid VisitId { get; private set; }
    public string VisitorName { get; private set; } = default!;
    public string AgentId { get; private set; } = default!;
    public CheckpointDirection Direction { get; private set; }
    public bool WasGranted { get; private set; }
    public bool WasCheckOut { get; private set; }
    public bool IsSecurityEvent { get; private set; }
    public ScanDenialReason? DenialReason { get; private set; }
    public bool RecordedInDegradedMode { get; private set; }
    public int? OverstayMinutes { get; private set; }
    public string Detail { get; private set; } = default!;
    public DateTimeOffset Timestamp { get; private set; }

    private ScanLogEntry() { } // EF Core

    public static ScanLogEntry Create(
        Guid visitId, string visitorName, string agentId, CheckpointDirection direction,
        ScanOutcome outcome, bool degradedMode, string detail, DateTimeOffset now)
    {
        return new ScanLogEntry
        {
            Id = Guid.NewGuid(),
            VisitId = visitId,
            VisitorName = visitorName,
            AgentId = agentId,
            Direction = direction,
            WasGranted = outcome.IsGranted,
            WasCheckOut = outcome.IsCheckOut,
            IsSecurityEvent = outcome.IsSecurityEvent,
            DenialReason = outcome.DenialReason,
            RecordedInDegradedMode = degradedMode,
            OverstayMinutes = outcome.IsCheckOut ? outcome.OverstayMinutesAtCheckOut : null,
            Detail = detail,
            Timestamp = now
        };
    }
}
