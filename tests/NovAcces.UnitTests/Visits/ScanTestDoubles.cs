using NovAcces.Application.Abstractions;
using NovAcces.Application.Visits;
using NovAcces.Domain.Entities;

namespace NovAcces.UnitTests.Visits;

/// <summary>
/// Doublures de test minimalistes partagées par les handlers de scan (QR et
/// code de secours) — pas de bibliothèque de mock référencée dans ce
/// scaffold (à remplacer par Moq/NSubstitute en Jalon 2 si le volume de
/// tests d'intégration le justifie).
/// </summary>
internal sealed class FakeClock : IDateTimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed class FakeSigningService : IQrSigningService
{
    public QrVerificationResult? NextVerification { get; set; }

    public string SignVisitToken(Guid visitId, Guid visitToken, DateTimeOffset expiresAt) => "signed";
    public QrVerificationResult VerifySignedToken(string signedPayload) =>
        NextVerification ?? new QrVerificationResult(false, null, null, null);
    public string SignDailyOfflineList(IReadOnlyCollection<OfflineListEntry> entries, DateTimeOffset issuedAt, DateTimeOffset expiresAt) => "signed-list";
    public OfflineListVerificationResult VerifyDailyOfflineList(string signedList) =>
        new(true, false, Array.Empty<OfflineListEntry>());
}

/// <summary>Résout toujours vers <see cref="ComputedHash"/>, quel que soit le code brut fourni.</summary>
internal sealed class FakeManualCodeService : IManualCodeService
{
    public string ComputedHash { get; set; } = "fake-hash";
    public (string RawCode, string CodeHash) GenerateCode() => ("ABCD-2345", ComputedHash);
    public string ComputeHash(string rawCode) => ComputedHash;
    public string ComputeLegacyHash(string rawCode) => ComputedHash;
}

internal sealed class FakeVisitRepository : IVisitRepository
{
    public Visit? VisitToReturn { get; set; }
    public Task<Visit?> GetForUpdateAsync(Guid visitToken, CancellationToken ct) => Task.FromResult(VisitToReturn);
    public Task<Visit?> GetForUpdateByManualCodeHashAsync(IReadOnlyList<string> candidateHashes, CancellationToken ct) => Task.FromResult(VisitToReturn);
    public Task<Visit?> GetForUpdateByIdAsync(Guid visitId, CancellationToken ct) => Task.FromResult(VisitToReturn);
    public Task<Visit?> GetByIdAsync(Guid visitId, CancellationToken ct) => Task.FromResult(VisitToReturn);
    public Task<Visit?> GetByTokenAsync(Guid visitToken, CancellationToken ct) => Task.FromResult<Visit?>(null);
    public Task AddAsync(Visit visit, CancellationToken ct) => Task.CompletedTask;
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

internal sealed class FakeScanLogRepository : IScanLogRepository
{
    public List<ScanLogEntry> Entries { get; } = new();
    public Task AddAsync(ScanLogEntry entry, CancellationToken ct) { Entries.Add(entry); return Task.CompletedTask; }
    public Task<IReadOnlyCollection<ScanLogEntry>> GetRecentAsync(int limit, string? query, CancellationToken ct)
        => Task.FromResult<IReadOnlyCollection<ScanLogEntry>>(Entries.AsReadOnly());
    public Task<(IReadOnlyCollection<ScanLogEntry> Items, int TotalCount)> GetPagedAsync(int page, int pageSize, string? query, CancellationToken ct)
        => Task.FromResult<(IReadOnlyCollection<ScanLogEntry>, int)>((Entries.AsReadOnly(), Entries.Count));
    public Task<IReadOnlyCollection<ScanLogEntry>> GetSinceAsync(DateTimeOffset sinceUtc, CancellationToken ct)
        => Task.FromResult<IReadOnlyCollection<ScanLogEntry>>(Entries.AsReadOnly());
    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
}

internal sealed class FakeScanEventBroadcaster : IScanEventBroadcaster
{
    public ScanBroadcastEvent? LastBroadcast { get; private set; }
    public Task BroadcastAsync(ScanBroadcastEvent scanEvent, CancellationToken ct)
    {
        LastBroadcast = scanEvent;
        return Task.CompletedTask;
    }
    public Task BroadcastOverstayAsync(OverstayBroadcastEvent overstay, CancellationToken ct) => Task.CompletedTask;
    public Task BroadcastConfirmationRequestedAsync(ConfirmationRequestedEvent requested, CancellationToken ct) => Task.CompletedTask;
    public Task BroadcastConfirmationResolvedAsync(Guid requestId, CancellationToken ct) => Task.CompletedTask;
    public HostVisitBroadcastEvent? LastHostVisitEvent { get; private set; }
    public Task BroadcastHostVisitEventAsync(HostVisitBroadcastEvent evt, CancellationToken ct)
    {
        LastHostVisitEvent = evt;
        return Task.CompletedTask;
    }
}

internal sealed class FakeExclusionList : IExclusionListService
{
    // Nom seul (comportement historique) : suffit pour les tests existants,
    // qui n'exercent pas la précision par email (voir ExclusionMatchKeyTests).
    public HashSet<string> ExcludedNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> IsExcludedAsync(string visitorName, string? visitorEmail, CancellationToken ct)
        => Task.FromResult(ExcludedNames.Contains(visitorName));
    public Task<IReadOnlyCollection<ExclusionMatchKey>> GetMatchKeysAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyCollection<ExclusionMatchKey>>(
            ExcludedNames.Select(n => new ExclusionMatchKey(ExclusionEntry.Normalize(n), null)).ToList());
    public Task<IReadOnlyList<ExclusionEntryView>> ListAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ExclusionEntryView>>(Array.Empty<ExclusionEntryView>());
    public Task<Guid> AddAsync(string displayName, string reason, string addedBy, string? email, CancellationToken ct)
        => Task.FromResult(Guid.NewGuid());
    public Task<ExclusionEntryView?> RemoveAsync(Guid id, CancellationToken ct)
        => Task.FromResult<ExclusionEntryView?>(null);
}

internal sealed class FakeHostDirectory : IHostDirectory
{
    public HostContact? Contact { get; set; } = new("Hôte Test", "hote@sicopa.local", null);
    public Task<HostContact?> FindAsync(string hostUserId, CancellationToken ct) => Task.FromResult(Contact);

    public Task<IReadOnlyDictionary<string, HostContact>> FindManyAsync(
        IReadOnlyCollection<string> hostUserIds, CancellationToken ct)
    {
        IReadOnlyDictionary<string, HostContact> result = Contact is null
            ? new Dictionary<string, HostContact>()
            : hostUserIds.Distinct().ToDictionary(id => id, _ => Contact);
        return Task.FromResult(result);
    }
}

internal sealed class FakeNotifications : INotificationService
{
    public List<HostEventNotification> HostEvents { get; } = new();

    public Task SendVisitInvitationAsync(VisitInvitationNotification notification, CancellationToken ct)
        => Task.CompletedTask;

    public Task NotifyHostAsync(HostEventNotification notification, CancellationToken ct)
    {
        HostEvents.Add(notification);
        return Task.CompletedTask;
    }

    public Task SendPasswordResetAsync(PasswordResetNotification notification, CancellationToken ct)
        => Task.CompletedTask;

    public Task NotifySureteConfirmationRequestAsync(SureteConfirmationRequestNotification notification, CancellationToken ct)
        => Task.CompletedTask;
}

/// <summary>
/// Doublure de transaction : exécute l'opération directement (pas de base en
/// test unitaire). La vraie sémantique transactionnelle/verrou est couverte par
/// NovAcces.IntegrationTests (test de concurrence anti-rejeu).
/// </summary>
internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken ct = default) => operation(ct);
}
