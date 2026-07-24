using Microsoft.Extensions.Logging.Abstractions;
using NovAcces.Application.Abstractions;
using NovAcces.Application.Visits;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;
using Xunit;

namespace NovAcces.UnitTests.Visits;

file sealed class FakeClock : IDateTimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
}

file sealed class FakeSigningService : IQrSigningService
{
    public string SignVisitToken(Guid visitId, Guid visitToken, DateTimeOffset expiresAt) => "signed-payload";
    public QrVerificationResult VerifySignedToken(string signedPayload) => new(false, null, null, null);
    public string SignDailyOfflineList(IReadOnlyCollection<OfflineListEntry> entries, DateTimeOffset issuedAt, DateTimeOffset expiresAt) => "signed-list";
    public OfflineListVerificationResult VerifyDailyOfflineList(string signedList) =>
        new(true, false, Array.Empty<OfflineListEntry>());
}

file sealed class FakeVisitRepository : IVisitRepository
{
    public Visit? AddedVisit { get; private set; }
    public Task<Visit?> GetForUpdateAsync(Guid visitToken, CancellationToken ct) => Task.FromResult<Visit?>(null);
    public Task<Visit?> GetByIdAsync(Guid visitId, CancellationToken ct) => Task.FromResult<Visit?>(null);
    public Task AddAsync(Visit visit, CancellationToken ct) { AddedVisit = visit; return Task.CompletedTask; }
    public Task<IReadOnlyCollection<Visit>> GetTodayActiveVisitsAsync(DateTimeOffset today, CancellationToken ct)
        => Task.FromResult<IReadOnlyCollection<Visit>>(Array.Empty<Visit>());
    public Task<IReadOnlyCollection<Visit>> GetOnSiteAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyCollection<Visit>>(Array.Empty<Visit>());
    public Task<IReadOnlyCollection<Visit>> GetByHostAsync(string hostUserId, int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyCollection<Visit>>(Array.Empty<Visit>());
    public Task<IReadOnlyCollection<KnownVisitor>> GetKnownVisitorsAsync(int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyCollection<KnownVisitor>>(Array.Empty<KnownVisitor>());
    public Task<bool> HasActiveVisitForVisitorAsync(string visitorName, CancellationToken ct)
        => Task.FromResult(false);
    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
}

file sealed class FakeExclusionListService : IExclusionListService
{
    public bool IsExcluded { get; set; }
    public Task<bool> IsExcludedAsync(string visitorName, CancellationToken ct) => Task.FromResult(IsExcluded);
    public Task<IReadOnlyList<ExclusionEntryView>> ListAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ExclusionEntryView>>(Array.Empty<ExclusionEntryView>());
    public Task AddAsync(string displayName, string reason, string addedBy, CancellationToken ct) => Task.CompletedTask;
    public Task<bool> RemoveAsync(Guid id, CancellationToken ct) => Task.FromResult(true);
}

file sealed class FakeNotificationService : INotificationService
{
    public VisitInvitationNotification? LastNotification { get; private set; }
    public bool ThrowOnSend { get; set; }

    public Task SendVisitInvitationAsync(VisitInvitationNotification notification, CancellationToken ct)
    {
        if (ThrowOnSend) throw new InvalidOperationException("Panne simulée du canal de notification.");
        LastNotification = notification;
        return Task.CompletedTask;
    }
}

public class CreateVisitHandlerTests
{
    [Fact]
    public async Task HandleAsync_ValidVisitor_SendsInvitationWithSignedPayload()
    {
        var clock = new FakeClock();
        var visits = new FakeVisitRepository();
        var notifications = new FakeNotificationService();
        var handler = new CreateVisitHandler(
            visits, new FakeSigningService(), clock, new FakeExclusionListService(),
            notifications, NullLogger<CreateVisitHandler>.Instance);

        var command = new CreateVisitCommand(
            "Jean Visiteur", "ACME SARL", "Livraison", "host-1",
            AccessMode.Unique, clock.UtcNow.AddHours(1), 60, "+2250700000000", "jean@example.com");

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.NotNull(notifications.LastNotification);
        Assert.Equal("signed-payload", notifications.LastNotification!.SignedQrPayload);
        Assert.Equal("Jean Visiteur", notifications.LastNotification.VisitorName);
        Assert.Equal(result.SignedQrPayload, notifications.LastNotification.SignedQrPayload);
    }

    [Fact]
    public async Task HandleAsync_NotificationChannelFails_VisitIsStillCreated()
    {
        // Le canal de notification est best-effort : une panne WhatsApp/email
        // ne doit jamais empêcher la création d'une visite dont le QR est
        // déjà signé et enregistré.
        var clock = new FakeClock();
        var visits = new FakeVisitRepository();
        var notifications = new FakeNotificationService { ThrowOnSend = true };
        var handler = new CreateVisitHandler(
            visits, new FakeSigningService(), clock, new FakeExclusionListService(),
            notifications, NullLogger<CreateVisitHandler>.Instance);

        var command = new CreateVisitCommand(
            "Jean Visiteur", "ACME SARL", "Livraison", "host-1",
            AccessMode.Unique, clock.UtcNow.AddHours(1), 60, "+2250700000000", "jean@example.com");

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.VisitId);
        Assert.NotNull(visits.AddedVisit);
        Assert.Equal("signed-payload", result.SignedQrPayload);
    }
}
