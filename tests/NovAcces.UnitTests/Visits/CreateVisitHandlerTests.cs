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
    public Task<Visit?> GetForUpdateByManualCodeHashAsync(IReadOnlyList<string> candidateHashes, CancellationToken ct) => Task.FromResult<Visit?>(null);
    public Task<Visit?> GetForUpdateByIdAsync(Guid visitId, CancellationToken ct) => Task.FromResult<Visit?>(null);
    public Task<Visit?> GetByIdAsync(Guid visitId, CancellationToken ct) => Task.FromResult<Visit?>(null);
    public Task<Visit?> GetByTokenAsync(Guid visitToken, CancellationToken ct) => Task.FromResult<Visit?>(null);
    public Task AddAsync(Visit visit, CancellationToken ct) { AddedVisit = visit; return Task.CompletedTask; }
    public Task<IReadOnlyCollection<Visit>> GetTodayActiveVisitsAsync(DateTimeOffset today, CancellationToken ct)
        => Task.FromResult<IReadOnlyCollection<Visit>>(Array.Empty<Visit>());
    public Task<IReadOnlyCollection<Visit>> GetOnSiteAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyCollection<Visit>>(Array.Empty<Visit>());
    public Task<IReadOnlyCollection<Visit>> GetByHostAsync(string hostUserId, int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyCollection<Visit>>(Array.Empty<Visit>());
    public Task<(IReadOnlyCollection<Visit> Items, int TotalCount)> GetPagedAsync(int page, int pageSize, string? query, CancellationToken ct)
        => Task.FromResult<(IReadOnlyCollection<Visit>, int)>((Array.Empty<Visit>(), 0));
    public Task<IReadOnlyDictionary<Guid, string>> GetHostUserIdsByVisitIdsAsync(IReadOnlyCollection<Guid> visitIds, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    public Task<IReadOnlyCollection<KnownVisitor>> GetKnownVisitorsAsync(int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyCollection<KnownVisitor>>(Array.Empty<KnownVisitor>());
    public Task<bool> HasActiveVisitForVisitorAsync(string visitorName, string visitorCompany, CancellationToken ct)
        => Task.FromResult(false);
    public Task ExpireStaleActiveVisitsAsync(string visitorName, string visitorCompany, DateTimeOffset now, CancellationToken ct)
        => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
}

file sealed class FakeExclusionListService : IExclusionListService
{
    public bool IsExcluded { get; set; }
    public Task<bool> IsExcludedAsync(string visitorName, CancellationToken ct) => Task.FromResult(IsExcluded);
    public Task<IReadOnlySet<string>> GetExcludedNormalizedNamesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
    public Task<IReadOnlyList<ExclusionEntryView>> ListAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ExclusionEntryView>>(Array.Empty<ExclusionEntryView>());
    public Task<Guid> AddAsync(string displayName, string reason, string addedBy, CancellationToken ct) => Task.FromResult(Guid.NewGuid());
    public Task<ExclusionEntryView?> RemoveAsync(Guid id, CancellationToken ct)
        => Task.FromResult<ExclusionEntryView?>(null);
}

file sealed class FakeManualCodeService : IManualCodeService
{
    public (string RawCode, string CodeHash) GenerateCode() => ("ABCD-2345", "fake-hash");
    public string ComputeHash(string rawCode) => "fake-hash";
    public string ComputeLegacyHash(string rawCode) => "fake-legacy-hash";
}

file sealed class FakeCurrentTenant : ICurrentTenant
{
    public string SiteId { get; init; } = "sicopa";
    public string SchemaName => $"site_{SiteId}";
    public bool IsResolved => true;
}

file sealed class FakeSiteDisplayNameProvider : ISiteDisplayNameProvider
{
    public string GetLabel(string siteId) => "SICOPA — Terminal portuaire";
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

    public Task NotifyHostAsync(HostEventNotification notification, CancellationToken ct)
        => Task.CompletedTask;

    public Task SendPasswordResetAsync(PasswordResetNotification notification, CancellationToken ct)
        => Task.CompletedTask;

    public Task NotifySureteConfirmationRequestAsync(SureteConfirmationRequestNotification notification, CancellationToken ct)
        => Task.CompletedTask;
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
            new FakeManualCodeService(), notifications, new FakeCurrentTenant(), new FakeSiteDisplayNameProvider(),
            NullLogger<CreateVisitHandler>.Instance);

        var command = new CreateVisitCommand(
            "Jean Visiteur", "ACME SARL", "Livraison", "host-1",
            AccessMode.Unique, clock.UtcNow.AddHours(1), 60, "+2250700000000", "jean@example.com");

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.NotNull(notifications.LastNotification);
        Assert.Equal("signed-payload", notifications.LastNotification!.SignedQrPayload);
        Assert.Equal("Jean Visiteur", notifications.LastNotification.VisitorName);
        Assert.Equal(result.SignedQrPayload, notifications.LastNotification.SignedQrPayload);

        // Le nom du site (billet enrichi, audit du 05/08/2026) doit être
        // résolu via ISiteDisplayNameProvider et joint à la notification.
        Assert.Equal("SICOPA — Terminal portuaire", notifications.LastNotification.SiteLabel);
        Assert.Equal("ACME SARL", notifications.LastNotification.VisitorCompany);
        Assert.Equal("Livraison", notifications.LastNotification.Motif);

        // Le code brut renvoyé (email + réponse HTTP) est distinct de son
        // empreinte persistée — seule cette dernière est stockée sur la visite.
        Assert.Equal("ABCD-2345", result.ManualCode);
        Assert.Equal("ABCD-2345", notifications.LastNotification.ManualCode);
        Assert.Equal("fake-hash", visits.AddedVisit!.ManualCodeHash);
        Assert.NotEqual(result.ManualCode, visits.AddedVisit.ManualCodeHash);
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
            new FakeManualCodeService(), notifications, new FakeCurrentTenant(), new FakeSiteDisplayNameProvider(),
            NullLogger<CreateVisitHandler>.Instance);

        var command = new CreateVisitCommand(
            "Jean Visiteur", "ACME SARL", "Livraison", "host-1",
            AccessMode.Unique, clock.UtcNow.AddHours(1), 60, "+2250700000000", "jean@example.com");

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.VisitId);
        Assert.NotNull(visits.AddedVisit);
        Assert.Equal("signed-payload", result.SignedQrPayload);
    }
}
