using Microsoft.Extensions.Logging.Abstractions;
using NovAcces.Application.Abstractions;
using NovAcces.Application.Visits;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;
using Xunit;

namespace NovAcces.UnitTests.Visits;

// Doublures partagées (FakeClock, FakeSigningService, FakeVisitRepository,
// FakeScanLogRepository, FakeScanEventBroadcaster, FakeExclusionList,
// FakeHostDirectory, FakeNotifications, FakeUnitOfWork) : voir ScanTestDoubles.cs
// — réutilisées telles quelles par ScanManualCodeHandlerTests.

public class ScanQrHandlerTests
{
    [Fact]
    public async Task HandleAsync_ExpiredCryptographicToken_IsRejectedAsSecurityEvent()
    {
        // Régression du bug identifié lors de l'audit de conformité du
        // 23/07/2026 : l'expiration du jeton (REQ-SEC-04) n'était jamais
        // vérifiée avant correction.
        var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };
        var signing = new FakeSigningService
        {
            NextVerification = new QrVerificationResult(
                IsValid: true,
                VisitId: Guid.NewGuid(),
                VisitToken: Guid.NewGuid(),
                ExpiresAt: clock.UtcNow.AddMinutes(-1)) // déjà expiré
        };
        var visits = new FakeVisitRepository();
        var logs = new FakeScanLogRepository();
        var handler = new ScanQrHandler(
            signing, visits, logs, clock,
            new FakeScanEventBroadcaster(), new FakeExclusionList(), new FakeHostDirectory(), new FakeNotifications(), new FakeUnitOfWork(), NullLogger<ScanQrHandler>.Instance);

        var result = await handler.HandleAsync(
            new ScanQrCommand("payload", CheckpointDirection.Entry, "SG-0417", false, true),
            CancellationToken.None);

        Assert.False(result.IsGranted);
        Assert.True(result.IsSecurityEvent);
        Assert.Equal("INVALID_SIGNATURE", result.VerdictCode);
        Assert.Contains(logs.Entries, e => e.Detail.Contains("expiré"));
    }

    [Fact]
    public async Task HandleAsync_NonExpiredToken_ProceedsToVisitLookup()
    {
        var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };
        var visitToken = Guid.NewGuid();
        var signing = new FakeSigningService
        {
            NextVerification = new QrVerificationResult(true, Guid.NewGuid(), visitToken, clock.UtcNow.AddMinutes(10))
        };
        var visit = Visit.Create("Test Visiteur", "Test SARL", "Motif", "host-1",
            AccessMode.Unique, clock.UtcNow, 60, null, null, false, clock.UtcNow);
        var visits = new FakeVisitRepository { VisitToReturn = visit };
        var logs = new FakeScanLogRepository();
        var handler = new ScanQrHandler(
            signing, visits, logs, clock,
            new FakeScanEventBroadcaster(), new FakeExclusionList(), new FakeHostDirectory(), new FakeNotifications(), new FakeUnitOfWork(), NullLogger<ScanQrHandler>.Instance);

        var result = await handler.HandleAsync(
            new ScanQrCommand("payload", CheckpointDirection.Entry, "SG-0417", false, true),
            CancellationToken.None);

        Assert.True(result.IsGranted);
    }

    [Fact]
    public async Task HandleAsync_VisitorPutOnExclusionListAfterQrIssued_IsDeniedAndLogged()
    {
        // La demande a été créée alors que le visiteur n'était PAS exclu
        // (isExcluded: false figé sur l'entité). La sûreté l'ajoute ensuite à la
        // liste : le handler doit relire la liste au scan et refuser.
        var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };
        var visitToken = Guid.NewGuid();
        var signing = new FakeSigningService
        {
            NextVerification = new QrVerificationResult(true, Guid.NewGuid(), visitToken, clock.UtcNow.AddMinutes(10))
        };
        var visit = Visit.Create("Koffi Yao", "Transit SARL", "Livraison", "host-1",
            AccessMode.Unique, clock.UtcNow, 60, null, null, isExcluded: false, clock.UtcNow);
        var visits = new FakeVisitRepository { VisitToReturn = visit };
        var logs = new FakeScanLogRepository();
        var exclusions = new FakeExclusionList();
        exclusions.ExcludedNames.Add("Koffi Yao");

        var handler = new ScanQrHandler(
            signing, visits, logs, clock,
            new FakeScanEventBroadcaster(), exclusions, new FakeHostDirectory(), new FakeNotifications(), new FakeUnitOfWork(), NullLogger<ScanQrHandler>.Instance);

        var result = await handler.HandleAsync(
            new ScanQrCommand("payload", CheckpointDirection.Entry, "SG-0417", false, true),
            CancellationToken.None);

        Assert.False(result.IsGranted);
        Assert.True(result.IsSecurityEvent);
        Assert.Equal("DENIED_Excluded", result.VerdictCode);
        Assert.False(visit.IsOnSite);
        Assert.Contains(logs.Entries, e => e.Detail.Contains("liste d'exclusion"));
    }

    [Fact]
    public async Task HandleAsync_Arrival_ThenDeparture_NotifiesTheHost()
    {
        // §1.3 et §1.6 : l'hôte est prévenu de l'arrivée ET du départ de son
        // visiteur, avec la durée de présence à la sortie.
        var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };
        var visitToken = Guid.NewGuid();
        var signing = new FakeSigningService
        {
            NextVerification = new QrVerificationResult(true, Guid.NewGuid(), visitToken, clock.UtcNow.AddHours(1))
        };
        var visit = Visit.Create("Awa Traoré", "ACME", "Réunion", "host-1",
            AccessMode.Unique, clock.UtcNow, 60, null, null, false, clock.UtcNow);
        var visits = new FakeVisitRepository { VisitToReturn = visit };
        var notifications = new FakeNotifications();

        var handler = new ScanQrHandler(
            signing, visits, new FakeScanLogRepository(), clock,
            new FakeScanEventBroadcaster(), new FakeExclusionList(), new FakeHostDirectory(),
            notifications, new FakeUnitOfWork(), NullLogger<ScanQrHandler>.Instance);

        await handler.HandleAsync(
            new ScanQrCommand("payload", CheckpointDirection.Entry, "SG-0417", false, true),
            CancellationToken.None);

        clock.UtcNow = clock.UtcNow.AddMinutes(45);
        var exit = await handler.HandleAsync(
            new ScanQrCommand("payload", CheckpointDirection.Exit, "SG-0417", false, true),
            CancellationToken.None);

        Assert.Equal(2, notifications.HostEvents.Count);
        Assert.Equal(HostEventKind.Arrival, notifications.HostEvents[0].Kind);

        var departure = notifications.HostEvents[1];
        Assert.Equal(HostEventKind.Departure, departure.Kind);
        Assert.Equal(45, departure.PresenceMinutes);
        Assert.Equal(45, exit.PresenceMinutes);
    }

    [Fact]
    public async Task HandleAsync_SuspectedDuplicate_AsksTheHostToVerify()
    {
        // §2 : quand le QR est présenté à l'entrée alors que le titulaire est
        // déjà sur site, l'hôte doit être invité à vérifier que son visiteur
        // est bien arrivé — c'est ce scan qui révèle une éventuelle copie.
        var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };
        var signing = new FakeSigningService
        {
            NextVerification = new QrVerificationResult(true, Guid.NewGuid(), Guid.NewGuid(), clock.UtcNow.AddHours(1))
        };
        var visit = Visit.Create("Awa Traoré", "ACME", "Réunion", "host-1",
            AccessMode.Unique, clock.UtcNow, 60, null, null, false, clock.UtcNow);
        visit.Scan(CheckpointDirection.Entry, true, clock.UtcNow, isOnExclusionList: false); // déjà entré
        var notifications = new FakeNotifications();

        var handler = new ScanQrHandler(
            signing, new FakeVisitRepository { VisitToReturn = visit }, new FakeScanLogRepository(), clock,
            new FakeScanEventBroadcaster(), new FakeExclusionList(), new FakeHostDirectory(),
            notifications, new FakeUnitOfWork(), NullLogger<ScanQrHandler>.Instance);

        var result = await handler.HandleAsync(
            new ScanQrCommand("payload", CheckpointDirection.Entry, "SG-0417", false, true),
            CancellationToken.None);

        Assert.False(result.IsGranted);
        Assert.True(result.IsSecurityEvent);
        Assert.Single(notifications.HostEvents);
        Assert.Equal(HostEventKind.SuspectedDuplicate, notifications.HostEvents[0].Kind);
    }

    [Fact]
    public async Task HandleAsync_DeniedScan_DoesNotNotifyTheHost()
    {
        // Un refus ordinaire (hors fenêtre) ne concerne pas l'hôte : on ne
        // l'inonde pas d'alertes pour chaque tentative ratée au poste.
        var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };
        var signing = new FakeSigningService
        {
            NextVerification = new QrVerificationResult(true, Guid.NewGuid(), Guid.NewGuid(), clock.UtcNow.AddDays(1))
        };
        var visit = Visit.Create("Awa Traoré", "ACME", "Réunion", "host-1",
            AccessMode.Unique, clock.UtcNow.AddHours(5), 60, null, null, false, clock.UtcNow);
        var notifications = new FakeNotifications();

        var handler = new ScanQrHandler(
            signing, new FakeVisitRepository { VisitToReturn = visit }, new FakeScanLogRepository(), clock,
            new FakeScanEventBroadcaster(), new FakeExclusionList(), new FakeHostDirectory(),
            notifications, new FakeUnitOfWork(), NullLogger<ScanQrHandler>.Instance);

        var result = await handler.HandleAsync(
            new ScanQrCommand("payload", CheckpointDirection.Entry, "SG-0417", false, true),
            CancellationToken.None);

        Assert.Equal("DENIED_TooEarly", result.VerdictCode);
        Assert.Empty(notifications.HostEvents);
    }
}
