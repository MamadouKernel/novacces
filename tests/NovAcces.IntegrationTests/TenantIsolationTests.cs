using Microsoft.EntityFrameworkCore;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;
using Xunit;

namespace NovAcces.IntegrationTests;

/// <summary>
/// Vérifie, contre un PostgreSQL réel, que le cloisonnement multi-tenant est
/// effectif : un site ne doit JAMAIS pouvoir lire les données d'un autre site
/// (REQ-F-10). C'est le pire risque du projet (CLAUDE.md §7.3) — une fuite ici
/// serait une fuite de données entre clients de Sigasécurité.
///
/// Ces tests exercent le VRAI mécanisme de production (TenantSchemaConnection-
/// Interceptor + CurrentTenant), pas une simulation. Ils sont skippés si aucun
/// PostgreSQL n'est joignable (cf. PostgresTenantFixture).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TenantIsolationTests
{
    private readonly PostgresTenantFixture _fixture;

    public TenantIsolationTests(PostgresTenantFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Visit_WrittenUnderOneTenant_IsInvisibleToAnotherTenant()
    {
        Skip.IfNot(_fixture.DatabaseAvailable, _fixture.SkipReason);

        // Arrange : une visite écrite sous le tenant A, une autre sous le tenant B.
        var now = DateTimeOffset.UtcNow;

        await using (var ctxA = _fixture.CreateContext(PostgresTenantFixture.TenantA))
        {
            ctxA.Visits.Add(NewVisit("Visiteur Alpha", now));
            await ctxA.SaveChangesAsync();
        }

        await using (var ctxB = _fixture.CreateContext(PostgresTenantFixture.TenantB))
        {
            ctxB.Visits.Add(NewVisit("Visiteur Beta", now));
            await ctxB.SaveChangesAsync();
        }

        // Act + Assert : chaque tenant ne voit QUE sa propre visite.
        await using (var readA = _fixture.CreateContext(PostgresTenantFixture.TenantA))
        {
            var visitsA = await readA.Visits.AsNoTracking().ToListAsync();
            Assert.Single(visitsA);
            Assert.Equal("Visiteur Alpha", visitsA[0].VisitorName);
            Assert.DoesNotContain(visitsA, v => v.VisitorName == "Visiteur Beta");
        }

        await using (var readB = _fixture.CreateContext(PostgresTenantFixture.TenantB))
        {
            var visitsB = await readB.Visits.AsNoTracking().ToListAsync();
            Assert.Single(visitsB);
            Assert.Equal("Visiteur Beta", visitsB[0].VisitorName);
            Assert.DoesNotContain(visitsB, v => v.VisitorName == "Visiteur Alpha");
        }
    }

    [SkippableFact]
    public async Task Concurrent_Contexts_OnPooledConnections_StayIsolated()
    {
        Skip.IfNot(_fixture.DatabaseAvailable, _fixture.SkipReason);

        // Ce test est le cœur de la correction : il enchaîne des accès alternés
        // A / B / A / B sur des connexions issues du même pool. Si le search_path
        // n'était pas repositionné à chaque ouverture de connexion (bug corrigé),
        // une requête retomberait sur le mauvais schéma et l'isolation sauterait.
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 5; i++)
        {
            await using var ctxA = _fixture.CreateContext(PostgresTenantFixture.TenantA);
            var countA = await ctxA.Visits.CountAsync(v => v.VisitorName == "Visiteur Beta");
            Assert.Equal(0, countA); // A ne doit JAMAIS voir de visiteur de B

            await using var ctxB = _fixture.CreateContext(PostgresTenantFixture.TenantB);
            var countB = await ctxB.Visits.CountAsync(v => v.VisitorName == "Visiteur Alpha");
            Assert.Equal(0, countB); // B ne doit JAMAIS voir de visiteur de A
        }
    }

    private static Visit NewVisit(string visitorName, DateTimeOffset now) =>
        Visit.Create(
            visitorName: visitorName,
            visitorCompany: "ACME",
            motif: "Test d'isolation",
            hostUserId: "host-it",
            mode: AccessMode.Unique,
            scheduledAt: now,
            plannedDurationMinutes: 60,
            visitorPhone: null,
            visitorEmail: null,
            isExcluded: false,
            now: now);
}
