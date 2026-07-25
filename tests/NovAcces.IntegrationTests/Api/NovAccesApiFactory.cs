using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
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

            // Site de test provisionné (idempotent) pour que la création de visite
            // et le scan disposent de leur schéma.
            new TenantProvisioningService(ConnectionString).ProvisionAsync(TestSite).GetAwaiter().GetResult();

            DatabaseAvailable = true;
        }
        catch (Exception ex)
        {
            DatabaseAvailable = false;
            SkipReason = $"PostgreSQL non joignable ({ex.GetType().Name}). Tests d'intégration API ignorés.";
        }
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
                ["ApiKeys:Terminals:0:Key"] = TestApiKey,
                ["ApiKeys:Terminals:0:SiteId"] = TestSite,
                ["ApiKeys:Terminals:0:Label"] = "Terminal Test Intégration",
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
