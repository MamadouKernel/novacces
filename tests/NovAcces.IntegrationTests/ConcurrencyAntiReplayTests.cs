using Microsoft.EntityFrameworkCore;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;
using NovAcces.Infrastructure.Persistence;
using Xunit;

namespace NovAcces.IntegrationTests;

/// <summary>
/// Anti-rejeu sous concurrence réelle (REQ-SEC-03, CLAUDE.md §7.1). Prouve que
/// deux présentations SIMULTANÉES du même QR à l'entrée ne peuvent pas passer
/// toutes les deux : le verrou pessimiste (SELECT … FOR UPDATE), désormais tenu
/// pour toute la transaction (IUnitOfWork), sérialise les scans concurrents.
///
/// Avant la correction, le verrou était relâché dès la lecture (aucune
/// transaction englobante) : ce test aurait alors pu accorder plusieurs entrées.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConcurrencyAntiReplayTests
{
    private readonly PostgresTenantFixture _fixture;

    public ConcurrencyAntiReplayTests(PostgresTenantFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task ConcurrentEntryScans_OfSameQr_GrantExactlyOne()
    {
        Skip.IfNot(_fixture.DatabaseAvailable, _fixture.SkipReason);

        // Arrange : une visite valide, pas encore sur site.
        var now = DateTimeOffset.UtcNow;
        Guid token;
        await using (var seed = _fixture.CreateContext(PostgresTenantFixture.TenantA))
        {
            var visit = Visit.Create(
                "Visiteur Concurrent", "ACME", "Test anti-rejeu", "host-it",
                AccessMode.Unique, now, 60, null, null, isExcluded: false, now);
            token = visit.VisitToken;
            seed.Visits.Add(visit);
            await seed.SaveChangesAsync();
        }

        // Act : 4 scans d'entrée strictement concurrents, chacun sur sa propre
        // connexion/transaction. Un léger délai à l'intérieur de la section
        // critique garantit que les tentatives se chevauchent réellement.
        const int concurrency = 4;
        var attempts = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(() => AttemptEntryAsync(token, now)))
            .ToArray();

        var outcomes = await Task.WhenAll(attempts);

        // Assert : exactement UNE entrée accordée ; les autres refusées.
        var granted = outcomes.Count(o => o.IsGranted);
        Assert.Equal(1, granted);

        // Et l'état final en base est cohérent : la visite est bien sur site.
        await using var check = _fixture.CreateContext(PostgresTenantFixture.TenantA);
        var stored = await check.Visits.AsNoTracking().SingleAsync(v => v.VisitToken == token);
        Assert.True(stored.IsOnSite);
    }

    private async Task<ScanOutcome> AttemptEntryAsync(Guid token, DateTimeOffset now)
    {
        await using var ctx = _fixture.CreateContext(PostgresTenantFixture.TenantA);
        var uow = new UnitOfWork(ctx);
        var repo = new VisitRepository(ctx);

        return await uow.ExecuteInTransactionAsync(async ct =>
        {
            var visit = await repo.GetForUpdateAsync(token, ct);
            Assert.NotNull(visit);

            var outcome = visit!.Scan(CheckpointDirection.Entry, true, now, isOnExclusionList: false);

            // Fenêtre pendant laquelle le verrou DOIT rester tenu : sans la
            // transaction englobante, une tentative concurrente s'y glisserait.
            await Task.Delay(100, ct);

            await repo.SaveChangesAsync(ct);
            return outcome;
        });
    }
}
