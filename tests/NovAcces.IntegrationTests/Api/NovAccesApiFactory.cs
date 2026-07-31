using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NovAcces.Infrastructure.Identity;
using NovAcces.Infrastructure.Persistence.Tenancy;

namespace NovAcces.IntegrationTests.Api;

/// <summary>
/// Démarre l'API réelle en mémoire (TestServer) pour tester le pipeline complet :
/// authentification (JWT + clé API), policies RBAC, résolution de tenant par
/// claim, 2FA. La configuration sensible est injectée ici (clé JWT de test, clé
/// API de test, chaîne de connexion) — indépendante des user-secrets du poste.
///
/// Environnement « Development » pour que l'amorçage (migration du schéma
/// identity + rôles + Admin) s'exécute au démarrage. Redirection HTTPS désactivée
/// (le TestServer est en HTTP).
/// </summary>
public sealed class NovAccesApiFactory : WebApplicationFactory<Program>
{
    // Base DÉDIÉE aux tests (novacces_test) — jamais la base de dev.
    public static string ConnectionString => TestDatabase.ConnectionString;

    public const string TestApiKey = "integration-test-api-key-0123456789";
    public const string TestSite = "sicopa";
    public const string TestSite2 = "sanpedro";
    public const string TestApiKeyMultiSite = "integration-test-api-key-multisite-9876";
    public const string AdminEmail = "admin@novacces.local";
    public const string AdminPassword = "ChangeMoi!2026Dev";

    public bool DatabaseAvailable { get; }
    public string? SkipReason { get; }

    public NovAccesApiFactory()
    {
        try
        {
            // Crée la base de test dédiée si nécessaire (jamais la base de dev).
            TestDatabase.EnsureCreated();

            using var probe = new NpgsqlConnection(ConnectionString);
            probe.Open();

            // Sites de test provisionnés (idempotent) pour que la création de
            // visite et le scan disposent de leur schéma. TestSite2 sert aux tests
            // de terminal multi-sites (une tablette qui sert plusieurs sites).
            var provisioning = new TenantProvisioningService(ConnectionString);
            provisioning.ProvisionAsync(TestSite).GetAwaiter().GetResult();
            provisioning.ProvisionAsync(TestSite2).GetAwaiter().GetResult();

            // Terminaux de test, enrôlés directement en base (les clés API ne
            // sont plus en configuration statique, voir TerminalDirectory) —
            // un mono-site et un multi-sites, avec des clés connues à l'avance
            // pour que les tests puissent les présenter dans X-Api-Key.
            var identityOptions = new DbContextOptionsBuilder<NovAccesIdentityDbContext>()
                .UseNpgsql(ConnectionString)
                .Options;
            using (var identityDb = new NovAccesIdentityDbContext(identityOptions))
            {
                identityDb.Database.Migrate();
                SeedTerminal(identityDb, "Terminal Test Intégration", TestApiKey, new[] { TestSite });
                SeedTerminal(identityDb, "Terminal Test Multi-sites", TestApiKeyMultiSite, new[] { TestSite, TestSite2 });
            }

            DatabaseAvailable = true;
        }
        catch (Exception ex)
        {
            DatabaseAvailable = false;
            SkipReason = $"PostgreSQL non joignable ({ex.GetType().Name}). Tests d'intégration API ignorés.";
        }
    }

    private static void SeedTerminal(
        NovAccesIdentityDbContext db, string label, string apiKey, string[] siteIds)
    {
        var hash = TerminalDirectory.ComputeKeyHash(apiKey);
        if (db.Terminals.Any(t => t.ApiKeyHash == hash))
            return; // idempotent d'un run de tests à l'autre (base persistée)

        db.Terminals.Add(Terminal.Create(label, hash, siteIds, DateTimeOffset.UtcNow));
        db.SaveChanges();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DisableHttpsRedirection"] = "true",
                ["ConnectionStrings:Postgres"] = ConnectionString,
                ["Jwt:SigningKey"] = "integration-tests-signing-key-at-least-32-bytes-long",
                ["Jwt:Issuer"] = "NovAcces",
                ["Jwt:Audience"] = "NovAcces",
                ["Jwt:ExpiryMinutes"] = "60",
                // Les terminaux de test (TestApiKey / TestApiKeyMultiSite) sont
                // désormais enrôlés directement en base — voir SeedTerminal
                // ci-dessus — plus de configuration statique ApiKeys:Terminals.
                ["SeedAdmin:Email"] = AdminEmail,
                ["SeedAdmin:Password"] = AdminPassword,
                // Le minuteur de supervision ne doit pas se déclencher pendant les
                // tests (intervalle très long) : les tests pilotent le scanner
                // explicitement (OverstayTests). Le scanner reste actif (Enabled).
                ["Overstay:ScanIntervalSeconds"] = "36000",
                // Rate limiting désactivé en test : la suite sérialisée dépasse
                // sinon les 30 req/min de la politique « sensitive ».
                ["RateLimiting:Disabled"] = "true",
            });
        });
    }
}
