using Microsoft.Extensions.Logging.Abstractions;
using NovAcces.Application.Abstractions;
using NovAcces.Application.Visits;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;
using Xunit;

namespace NovAcces.UnitTests.Visits;

/// <summary>
/// Doublures de test minimalistes (pas de bibliothèque de mock référencée
/// dans ce scaffold — à remplacer par Moq/NSubstitute en Jalon 2 si le
/// volume de tests d'intégration le justifie).
/// </summary>
file sealed class FakeClock : IDateTimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
}

file sealed class FakeSigningService : IQrSigningService
{
    public QrVerificationResult? NextVerification { get; set; }

    public string SignVisitToken(Guid visitId, Guid visitToken, DateTimeOffset expiresAt) => "signed";
    public QrVerificationResult VerifySignedToken(string signedPayload) =>
        NextVerification ?? new QrVerificationResult(false, null, null, null);
    public string SignDailyOfflineList(IReadOnlyCollection<OfflineListEntry> entries, DateTimeOffset issuedAt, DateTimeOffset expiresAt) => "signed-list";
    public OfflineListVerificationResult VerifyDailyOfflineList(string signedList) =>
        new(true, false, Array.Empty<OfflineListEntry>());
}

file sealed class FakeVisitRepository : IVisitRepository
{
    public Visit? VisitToReturn { get; set; }
    public Task<Visit?> GetForUpdateAsync(Guid visitToken, CancellationToken ct) => Task.FromResult(VisitToReturn);
    public Task<Visit?> GetByIdAsync(Guid visitId, CancellationToken ct) => Task.FromResult(VisitToReturn);
    public Task AddAsync(Visit visit, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyCollection<Visit>> GetTodayActiveVisitsAsync(DateTimeOffset today, CancellationToken ct)
        => Task.FromResult<IReadOnlyCollection<Visit>>(Array.Empty<Visit>());
    public Task<IReadOnlyCollection<Visit>> GetOnSiteAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyCollection<Visit>>(Array.Empty<Visit>());
    public Task<IReadOnlyCollection<Visit>> GetByHostAsync(string hostUserId, int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyCollection<Visit>>(Array.Empty<Visit>());
    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
}

file sealed class FakeScanLogRepository : IScanLogRepository
{
    public List<ScanLogEntry> Entries { get; } = new();
    public Task AddAsync(ScanLogEntry entry, CancellationToken ct) { Entries.Add(entry); return Task.CompletedTask; }
    public Task<IReadOnlyCollection<ScanLogEntry>> GetRecentAsync(int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyCollection<ScanLogEntry>>(Entries.AsReadOnly());
    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
}

file sealed class FakeScanEventBroadcaster : IScanEventBroadcaster
{
    public ScanBroadcastEvent? LastBroadcast { get; private set; }
    public Task BroadcastAsync(ScanBroadcastEvent scanEvent, CancellationToken ct)
    {
        LastBroadcast = scanEvent;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Doublure de transaction : exécute l'opération directement (pas de base en
/// test unitaire). La vraie sémantique transactionnelle/verrou est couverte par
/// NovAcces.IntegrationTests (test de concurrence anti-rejeu).
/// </summary>
file sealed class FakeUnitOfWork : IUnitOfWork
{
    public Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken ct = default) => operation(ct);
}

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
            new FakeScanEventBroadcaster(), new FakeUnitOfWork(), NullLogger<ScanQrHandler>.Instance);

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
            new FakeScanEventBroadcaster(), new FakeUnitOfWork(), NullLogger<ScanQrHandler>.Instance);

        var result = await handler.HandleAsync(
            new ScanQrCommand("payload", CheckpointDirection.Entry, "SG-0417", false, true),
            CancellationToken.None);

        Assert.True(result.IsGranted);
    }
}
