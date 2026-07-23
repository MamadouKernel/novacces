using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;
using Xunit;

namespace NovAcces.UnitTests.Visits;

/// <summary>
/// Ces tests reproduisent, en conditions réelles (Domain pur, sans base ni
/// framework), les scénarios validés sur la maquette de démonstration du
/// 22 juillet 2026. Toute divergence de comportement par rapport à ces tests
/// doit être traitée comme une régression fonctionnelle vis-à-vis du client.
/// </summary>
public class VisitScanTests
{
    private static Visit CreateUniqueVisit(DateTimeOffset scheduledAt, DateTimeOffset now, bool excluded = false) =>
        Visit.Create("Jean-Marc Kouassi", "CFAO Motors", "Livraison", "host-1",
            AccessMode.Unique, scheduledAt, plannedDurationMinutes: 60,
            visitorPhone: null, visitorEmail: null, isExcluded: excluded, now: now);

    [Fact]
    public void Scan_WithinWindow_AtEntry_IsGranted()
    {
        var now = DateTimeOffset.UtcNow;
        var visit = CreateUniqueVisit(scheduledAt: now, now: now);

        var outcome = visit.Scan(CheckpointDirection.Entry, isBusinessDay: true, now);

        Assert.True(outcome.IsGranted);
        Assert.True(visit.IsOnSite);
        Assert.Equal(VisitStatus.Consumed, visit.Status);
    }

    [Fact]
    public void Scan_TooEarly_IsDeniedAsSecurityEvent()
    {
        var now = DateTimeOffset.UtcNow;
        var visit = CreateUniqueVisit(scheduledAt: now.AddMinutes(130), now: now);

        var outcome = visit.Scan(CheckpointDirection.Entry, isBusinessDay: true, now);

        Assert.False(outcome.IsGranted);
        Assert.True(outcome.IsSecurityEvent);
        Assert.Equal(ScanDenialReason.TooEarly, outcome.DenialReason);
    }

    [Fact]
    public void Scan_TooLate_IsDeniedAsSecurityEvent()
    {
        var now = DateTimeOffset.UtcNow;
        var visit = CreateUniqueVisit(scheduledAt: now.AddHours(-19), now: now.AddHours(-19));

        var outcome = visit.Scan(CheckpointDirection.Entry, isBusinessDay: true, now);

        Assert.False(outcome.IsGranted);
        Assert.True(outcome.IsSecurityEvent);
        Assert.Equal(ScanDenialReason.TooLate, outcome.DenialReason);
    }

    [Fact]
    public void ReScan_AtEntry_WhileOnSite_IsDeniedAsSuspectedDuplicate()
    {
        // Scénario "copie volée" de la démonstration : le titulaire scanne à
        // l'entrée alors que son QR (copié) a déjà servi à faire entrer
        // quelqu'un d'autre.
        var now = DateTimeOffset.UtcNow;
        var visit = CreateUniqueVisit(scheduledAt: now, now: now);
        visit.Scan(CheckpointDirection.Entry, isBusinessDay: true, now); // le "voleur" entre

        var outcome = visit.Scan(CheckpointDirection.Entry, isBusinessDay: true, now.AddMinutes(2));

        Assert.False(outcome.IsGranted);
        Assert.True(outcome.IsSecurityEvent);
        Assert.Equal(ScanDenialReason.SuspectedDuplicate, outcome.DenialReason);
    }

    [Fact]
    public void Scan_AtExit_WhileOnSite_ChecksOutSuccessfully()
    {
        var now = DateTimeOffset.UtcNow;
        var visit = CreateUniqueVisit(scheduledAt: now, now: now);
        visit.Scan(CheckpointDirection.Entry, isBusinessDay: true, now);

        var outcome = visit.Scan(CheckpointDirection.Exit, isBusinessDay: true, now.AddMinutes(30));

        Assert.True(outcome.IsGranted);
        Assert.True(outcome.IsCheckOut);
        Assert.False(outcome.IsSecurityEvent);
        Assert.False(visit.IsOnSite);
        Assert.True(visit.HasCompletedCycle);
    }

    [Fact]
    public void Scan_AtExit_WithoutActiveEntry_IsDeniedButNotSecurityEvent()
    {
        var now = DateTimeOffset.UtcNow;
        var visit = CreateUniqueVisit(scheduledAt: now, now: now);

        var outcome = visit.Scan(CheckpointDirection.Exit, isBusinessDay: true, now);

        Assert.False(outcome.IsGranted);
        Assert.False(outcome.IsSecurityEvent); // erreur opérationnelle, pas une fraude
        Assert.Equal(ScanDenialReason.NoActiveEntry, outcome.DenialReason);
    }

    [Fact]
    public void Scan_AfterCompletedCycle_IsDeniedAsCycleClosed()
    {
        // L'anti-rejeu porte sur le cycle complet entrée/sortie, pas sur le
        // scan brut (raffinement demandé lors de la démonstration).
        var now = DateTimeOffset.UtcNow;
        var visit = CreateUniqueVisit(scheduledAt: now, now: now);
        visit.Scan(CheckpointDirection.Entry, isBusinessDay: true, now);
        visit.Scan(CheckpointDirection.Exit, isBusinessDay: true, now.AddMinutes(30));

        var outcome = visit.Scan(CheckpointDirection.Entry, isBusinessDay: true, now.AddMinutes(45));

        Assert.False(outcome.IsGranted);
        Assert.True(outcome.IsSecurityEvent);
        Assert.Equal(ScanDenialReason.CycleAlreadyClosed, outcome.DenialReason);
    }

    [Fact]
    public void Scan_RevokedVisit_AtEntry_IsDenied()
    {
        var now = DateTimeOffset.UtcNow;
        var visit = CreateUniqueVisit(scheduledAt: now, now: now);
        visit.Revoke();

        var outcome = visit.Scan(CheckpointDirection.Entry, isBusinessDay: true, now);

        Assert.False(outcome.IsGranted);
        Assert.Equal(ScanDenialReason.Revoked, outcome.DenialReason);
    }

    [Fact]
    public void Scan_RevokedVisit_WhileOnSite_ExitIsStillAllowed()
    {
        // Principe de sûreté : on ne bloque jamais une sortie, même révoquée.
        var now = DateTimeOffset.UtcNow;
        var visit = CreateUniqueVisit(scheduledAt: now, now: now);
        visit.Scan(CheckpointDirection.Entry, isBusinessDay: true, now);
        visit.Revoke();

        var outcome = visit.Scan(CheckpointDirection.Exit, isBusinessDay: true, now.AddMinutes(10));

        Assert.True(outcome.IsGranted);
        Assert.True(outcome.IsCheckOut);
        Assert.False(visit.IsOnSite);
    }

    [Fact]
    public void Scan_ExcludedVisitor_AtEntry_IsDeniedGenerically()
    {
        var now = DateTimeOffset.UtcNow;
        var visit = CreateUniqueVisit(scheduledAt: now, now: now, excluded: true);

        var outcome = visit.Scan(CheckpointDirection.Entry, isBusinessDay: true, now);

        Assert.False(outcome.IsGranted);
        Assert.True(outcome.IsSecurityEvent);
        Assert.Equal(ScanDenialReason.Excluded, outcome.DenialReason);
    }

    [Fact]
    public void Scan_ThirtyDaysMode_OnNonBusinessDay_IsDenied()
    {
        var now = DateTimeOffset.UtcNow;
        var visit = Visit.Create("Fatou Bamba", "Bureau Veritas", "Inspection", "host-1",
            AccessMode.ThirtyDays, scheduledAt: null, plannedDurationMinutes: 240,
            visitorPhone: null, visitorEmail: null, isExcluded: false, now: now);

        var outcome = visit.Scan(CheckpointDirection.Entry, isBusinessDay: false, now);

        Assert.False(outcome.IsGranted);
        Assert.Equal(ScanDenialReason.NonBusinessDay, outcome.DenialReason);
    }

    [Fact]
    public void Scan_ThirtyDaysMode_CanCheckInAndOutMultipleTimes()
    {
        var now = DateTimeOffset.UtcNow;
        var visit = Visit.Create("Fatou Bamba", "Bureau Veritas", "Inspection", "host-1",
            AccessMode.ThirtyDays, scheduledAt: null, plannedDurationMinutes: 240,
            visitorPhone: null, visitorEmail: null, isExcluded: false, now: now);

        var in1 = visit.Scan(CheckpointDirection.Entry, isBusinessDay: true, now);
        var out1 = visit.Scan(CheckpointDirection.Exit, isBusinessDay: true, now.AddHours(2));
        var in2 = visit.Scan(CheckpointDirection.Entry, isBusinessDay: true, now.AddDays(1));

        Assert.True(in1.IsGranted);
        Assert.True(out1.IsCheckOut);
        Assert.True(in2.IsGranted); // pas de cycle unique à clore en mode 30 jours
    }

    [Fact]
    public void Scan_ThirtyDaysMode_AfterThirtyDayPeriod_IsDenied()
    {
        // Correction du 23/07/2026 : un accès "30 jours" n'est pas permanent
        // (REQ-F-05) — gap identifié lors de l'audit de conformité du scaffold.
        var now = DateTimeOffset.UtcNow;
        var visit = Visit.Create("Fatou Bamba", "Bureau Veritas", "Inspection", "host-1",
            AccessMode.ThirtyDays, scheduledAt: null, plannedDurationMinutes: 240,
            visitorPhone: null, visitorEmail: null, isExcluded: false, now: now);

        var outcome = visit.Scan(CheckpointDirection.Entry, isBusinessDay: true, now.AddDays(31));

        Assert.False(outcome.IsGranted);
        Assert.True(outcome.IsSecurityEvent);
        Assert.Equal(ScanDenialReason.TooLate, outcome.DenialReason);
    }

    [Fact]
    public void OverstayAlert_FirstDetection_ReturnsLevelOne()
    {
        var now = DateTimeOffset.UtcNow;
        var visit = CreateUniqueVisit(scheduledAt: now, now: now);
        visit.Scan(CheckpointDirection.Entry, isBusinessDay: true, now); // durée prévue : 60 min

        var level = visit.EvaluateOverstayAlertLevel(now.AddMinutes(75), TimeSpan.FromMinutes(15));

        Assert.Equal(1, level);
    }

    [Fact]
    public void OverstayAlert_BeforeReminderInterval_DoesNotEscalate()
    {
        var now = DateTimeOffset.UtcNow;
        var visit = CreateUniqueVisit(scheduledAt: now, now: now);
        visit.Scan(CheckpointDirection.Entry, isBusinessDay: true, now);
        visit.EvaluateOverstayAlertLevel(now.AddMinutes(75), TimeSpan.FromMinutes(15)); // niveau 1

        var levelTooSoon = visit.EvaluateOverstayAlertLevel(now.AddMinutes(80), TimeSpan.FromMinutes(15));

        Assert.Equal(0, levelTooSoon); // pas encore l'heure du rappel n°2
    }

    [Fact]
    public void OverstayAlert_AfterReminderInterval_Escalates()
    {
        var now = DateTimeOffset.UtcNow;
        var visit = CreateUniqueVisit(scheduledAt: now, now: now);
        visit.Scan(CheckpointDirection.Entry, isBusinessDay: true, now);
        visit.EvaluateOverstayAlertLevel(now.AddMinutes(75), TimeSpan.FromMinutes(15)); // niveau 1

        var level2 = visit.EvaluateOverstayAlertLevel(now.AddMinutes(91), TimeSpan.FromMinutes(15));

        Assert.Equal(2, level2);
    }

    [Fact]
    public void CheckOut_ReportsOverstayMinutes_WhenLate()
    {
        var now = DateTimeOffset.UtcNow;
        var visit = CreateUniqueVisit(scheduledAt: now, now: now); // durée prévue : 60 min
        visit.Scan(CheckpointDirection.Entry, isBusinessDay: true, now);

        var outcome = visit.Scan(CheckpointDirection.Exit, isBusinessDay: true, now.AddMinutes(94));

        Assert.True(outcome.IsCheckOut);
        Assert.Equal(34, outcome.OverstayMinutesAtCheckOut);
    }
}
