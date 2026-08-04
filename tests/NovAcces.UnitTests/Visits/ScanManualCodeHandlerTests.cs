using Microsoft.Extensions.Logging.Abstractions;
using NovAcces.Application.Abstractions;
using NovAcces.Application.Visits;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;
using Xunit;

namespace NovAcces.UnitTests.Visits;

/// <summary>
/// Scan par code de secours — mêmes garanties de sûreté que ScanQrHandlerTests
/// (anti-rejeu, liste d'exclusion relue en direct, cycle entrée/sortie),
/// puisque les deux handlers délèguent au même ScanExecutionCore. Ce fichier
/// ne reteste donc pas toute la matrice de ScanQrHandlerTests : seulement ce
/// qui est spécifique au chemin "code" (résolution par empreinte, code
/// introuvable, AuthMethod journalisé) plus un cas représentatif de chaque
/// garantie partagée, pour prouver que la délégation fonctionne réellement.
/// </summary>
public class ScanManualCodeHandlerTests
{
    private static ScanManualCodeHandler MakeHandler(
        FakeVisitRepository visits, FakeScanLogRepository logs, FakeClock clock,
        FakeExclusionList? exclusions = null, FakeNotifications? notifications = null,
        FakeManualCodeService? manualCode = null) =>
        new(
            manualCode ?? new FakeManualCodeService(), visits, logs, clock,
            new FakeScanEventBroadcaster(), exclusions ?? new FakeExclusionList(),
            new FakeHostDirectory(), notifications ?? new FakeNotifications(),
            new FakeUnitOfWork(), NullLogger<ScanManualCodeHandler>.Instance);

    [Fact]
    public async Task HandleAsync_KnownCode_GrantsEntryAndLogsManualCodeAuthMethod()
    {
        var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };
        var visit = Visit.Create("Test Visiteur", "Test SARL", "Motif", "host-1",
            AccessMode.Unique, clock.UtcNow, 60, null, null, false, clock.UtcNow);
        visit.AssignManualCode("stored-hash");
        var visits = new FakeVisitRepository { VisitToReturn = visit };
        var logs = new FakeScanLogRepository();
        var handler = MakeHandler(visits, logs, clock);

        var result = await handler.HandleAsync(
            new ScanManualCodeCommand("ABCD-2345", CheckpointDirection.Entry, "SG-0417", false, true),
            CancellationToken.None);

        Assert.True(result.IsGranted);
        Assert.Equal("GRANTED", result.VerdictCode);
        Assert.Single(logs.Entries);
        Assert.Equal(ScanAuthMethod.ManualCode, logs.Entries[0].AuthMethod);
    }

    [Fact]
    public async Task HandleAsync_UnknownCode_IsRejectedAsInvalidCode()
    {
        // Aucune visite ne résout ce hash (visite absente/révoquée/code
        // mal saisi) — contrairement au QR, il n'y a pas de vérification
        // cryptographique hors base : "introuvable en base" EST le refus.
        var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };
        var visits = new FakeVisitRepository { VisitToReturn = null };
        var logs = new FakeScanLogRepository();
        var handler = MakeHandler(visits, logs, clock);

        var result = await handler.HandleAsync(
            new ScanManualCodeCommand("ZZZZ-0000", CheckpointDirection.Entry, "SG-0417", false, true),
            CancellationToken.None);

        Assert.False(result.IsGranted);
        Assert.True(result.IsSecurityEvent);
        Assert.Equal("INVALID_CODE", result.VerdictCode);
        Assert.Contains(logs.Entries, e => e.AuthMethod == ScanAuthMethod.ManualCode);
    }

    [Fact]
    public async Task HandleAsync_VisitorPutOnExclusionListAfterCodeIssued_IsDeniedAndLogged()
    {
        // Même garantie que pour le QR (REQ-F-11) : la liste est relue au
        // scan, pas figée à l'émission du code.
        var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };
        var visit = Visit.Create("Koffi Yao", "Transit SARL", "Livraison", "host-1",
            AccessMode.Unique, clock.UtcNow, 60, null, null, isExcluded: false, clock.UtcNow);
        visit.AssignManualCode("stored-hash");
        var visits = new FakeVisitRepository { VisitToReturn = visit };
        var exclusions = new FakeExclusionList();
        exclusions.ExcludedNames.Add("Koffi Yao");
        var handler = MakeHandler(visits, new FakeScanLogRepository(), clock, exclusions);

        var result = await handler.HandleAsync(
            new ScanManualCodeCommand("ABCD-2345", CheckpointDirection.Entry, "SG-0417", false, true),
            CancellationToken.None);

        Assert.False(result.IsGranted);
        Assert.True(result.IsSecurityEvent);
        Assert.Equal("DENIED_Excluded", result.VerdictCode);
        Assert.False(visit.IsOnSite);
    }

    [Fact]
    public async Task HandleAsync_AlreadyOnSite_IsSuspectedDuplicateAndNotifiesHost()
    {
        var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };
        var visit = Visit.Create("Awa Traoré", "ACME", "Réunion", "host-1",
            AccessMode.Unique, clock.UtcNow, 60, null, null, false, clock.UtcNow);
        visit.AssignManualCode("stored-hash");
        visit.Scan(CheckpointDirection.Entry, true, clock.UtcNow, isOnExclusionList: false); // déjà entré
        var notifications = new FakeNotifications();
        var handler = MakeHandler(
            new FakeVisitRepository { VisitToReturn = visit }, new FakeScanLogRepository(), clock,
            notifications: notifications);

        var result = await handler.HandleAsync(
            new ScanManualCodeCommand("ABCD-2345", CheckpointDirection.Entry, "SG-0417", false, true),
            CancellationToken.None);

        Assert.False(result.IsGranted);
        Assert.True(result.IsSecurityEvent);
        Assert.Single(notifications.HostEvents);
        Assert.Equal(HostEventKind.SuspectedDuplicate, notifications.HostEvents[0].Kind);
    }

    [Fact]
    public async Task HandleAsync_ExitWithoutActiveEntry_IsDenied()
    {
        var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };
        var visit = Visit.Create("Awa Traoré", "ACME", "Réunion", "host-1",
            AccessMode.Unique, clock.UtcNow, 60, null, null, false, clock.UtcNow);
        visit.AssignManualCode("stored-hash");
        var handler = MakeHandler(
            new FakeVisitRepository { VisitToReturn = visit }, new FakeScanLogRepository(), clock);

        var result = await handler.HandleAsync(
            new ScanManualCodeCommand("ABCD-2345", CheckpointDirection.Exit, "SG-0417", false, true),
            CancellationToken.None);

        Assert.False(result.IsGranted);
        Assert.Equal("DENIED_NoActiveEntry", result.VerdictCode);
    }
}
