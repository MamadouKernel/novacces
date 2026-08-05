using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;
using NovAcces.Infrastructure.Identity;
using NovAcces.Infrastructure.Persistence;
using NovAcces.Infrastructure.Persistence.Tenancy;
using Xunit;

namespace NovAcces.IntegrationTests.Api;

/// <summary>
/// ISiteDataResetService ("reset-site-data", CLI uniquement) vide COMPLÈTEMENT
/// un site pour repartir de zéro en phase de test/pilote. Vérifie le périmètre
/// exact : le schéma du site est vidé, les artefacts du schéma partagé
/// "identity" propres à CE site disparaissent, MAIS un terminal partagé avec
/// un AUTRE site survit (seulement détaché de celui réinitialisé) — c'est la
/// distinction la plus facile à casser par erreur dans cette implémentation.
///
/// Utilise un site JETABLE (jamais TestSite/TestSite2, partagés par toute la
/// suite via ApiCollection) : cette opération est destructrice, la faire
/// porter sur un site partagé casserait tous les autres tests de la collection.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SiteDataResetServiceTests
{
    private readonly NovAccesApiFactory _factory;

    public SiteDataResetServiceTests(NovAccesApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task ResetSiteAsync_WipesSiteSchemaAndIdentityArtifacts_ButKeepsSharedTerminalAttachedToOtherSite()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var siteId = "resettest" + Guid.NewGuid().ToString("N")[..8];

        // --- Amorçage : le site, ses artefacts identity, et une visite. ---
        Guid dedicatedTerminalId, sharedTerminalId;
        using (var setup = _factory.Services.CreateScope())
        {
            var provisioning = setup.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
            await provisioning.ProvisionAsync(siteId);

            var identityDb = setup.ServiceProvider.GetRequiredService<NovAccesIdentityDbContext>();

            identityDb.Users.Add(new ApplicationUser
            {
                UserName = $"hote-{siteId}@test.local", Email = $"hote-{siteId}@test.local",
                DisplayName = "Hôte Test Reset", SiteId = siteId, EmailConfirmed = true,
            });

            var dedicated = Terminal.Create(
                $"Dédié {siteId}", TerminalDirectory.ComputeKeyHash($"dedicated-{siteId}"), new[] { siteId }, DateTimeOffset.UtcNow);
            identityDb.Terminals.Add(dedicated);
            dedicatedTerminalId = dedicated.Id;

            // Partagé avec TestSite2 : doit SURVIVRE au reset de siteId, juste détaché de celui-ci.
            var shared = Terminal.Create(
                $"Partagé {siteId}", TerminalDirectory.ComputeKeyHash($"shared-{siteId}"),
                new[] { siteId, NovAccesApiFactory.TestSite2 }, DateTimeOffset.UtcNow);
            identityDb.Terminals.Add(shared);
            sharedTerminalId = shared.Id;

            // Matricule GLOBALEMENT unique (identity.agent_registry n'a que
            // Matricule en clé — un agent ne peut être actif que sur un seul
            // site à la fois, voir CLAUDE.md) : suffixé par siteId pour ne
            // jamais entrer en collision avec les restes d'une exécution
            // précédente de ce test (site jetable, jamais nettoyé après coup,
            // même précédent que TenantProvisioningTests).
            identityDb.AgentRegistry.Add(AgentRegistryEntry.Create($"MAT-{siteId}", siteId, DateTimeOffset.UtcNow));
            // TokenHash a un index unique : suffixé par siteId, même raison
            // que le matricule ci-dessus (site jetable jamais nettoyé entre
            // exécutions de ce test).
            identityDb.RefreshSessions.Add(RefreshSession.Create(
                "user", "reset-test-subject", "Test", siteId, $"fake-token-hash-{siteId}",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)));

            await identityDb.SaveChangesAsync();

            var tenant = new CurrentTenant();
            tenant.Resolve(siteId);
            var siteDb = new NovAccesDbContext(
                new DbContextOptionsBuilder<NovAccesDbContext>()
                    .UseNpgsql(NovAccesApiFactory.ConnectionString)
                    .AddInterceptors(new TenantSchemaConnectionInterceptor(tenant))
                    .Options,
                tenant);
            siteDb.Visits.Add(Visit.Create(
                "Visiteur Test", "Société Test", "Motif test", "host-reset-test",
                AccessMode.Unique, DateTimeOffset.UtcNow, 60, null, null, false, DateTimeOffset.UtcNow));
            await siteDb.SaveChangesAsync();
        }

        // --- Act ---
        using (var actScope = _factory.Services.CreateScope())
        {
            var reset = actScope.ServiceProvider.GetRequiredService<ISiteDataResetService>();
            await reset.ResetSiteAsync(siteId, CancellationToken.None);
        }

        // --- Assert : périmètre exact, sur des scopes/instances fraîches (pas de cache EF résiduel). ---
        using (var assertScope = _factory.Services.CreateScope())
        {
            var identityDb = assertScope.ServiceProvider.GetRequiredService<NovAccesIdentityDbContext>();

            Assert.False(await identityDb.Users.AnyAsync(u => u.SiteId == siteId),
                "Le compte rattaché au site réinitialisé aurait dû être supprimé.");
            Assert.False(await identityDb.Terminals.AnyAsync(t => t.Id == dedicatedTerminalId),
                "Le terminal dédié à ce seul site aurait dû être supprimé.");
            Assert.False(await identityDb.AgentRegistry.AnyAsync(a => a.SiteId == siteId),
                "Le registre d'agents de ce site aurait dû être vidé.");
            Assert.False(await identityDb.RefreshSessions.AnyAsync(r => r.SiteId == siteId),
                "Les sessions de rafraîchissement de ce site auraient dû être supprimées.");

            var survivingShared = await identityDb.Terminals.SingleAsync(t => t.Id == sharedTerminalId);
            Assert.DoesNotContain(siteId, survivingShared.SiteIds);
            Assert.Contains(NovAccesApiFactory.TestSite2, survivingShared.SiteIds);

            // GetSiteIdsAsync (non caché), pas ExistsAsync (caché ~30s) — même
            // piège que dans SiteDataResetService, cette fois côté assertion :
            // le schéma vient d'être re-créé il y a quelques millisecondes.
            var sites = assertScope.ServiceProvider.GetRequiredService<ISiteCatalog>();
            var siteIds = await sites.GetSiteIdsAsync(CancellationToken.None);
            Assert.Contains(siteId, siteIds);
        }

        using (var visitAssertScope = _factory.Services.CreateScope())
        {
            visitAssertScope.ServiceProvider.GetRequiredService<CurrentTenant>().Resolve(siteId);
            var siteDb = visitAssertScope.ServiceProvider.GetRequiredService<NovAccesDbContext>();
            Assert.Equal(0, await siteDb.Visits.CountAsync());
        }
    }
}
