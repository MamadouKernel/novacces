using Microsoft.EntityFrameworkCore;
using Npgsql;
using NovAcces.Infrastructure.Persistence;
using NovAcces.Infrastructure.Persistence.Tenancy;

namespace NovAcces.IntegrationTests;

/// <summary>
/// Fixture d'intégration branchée sur un PostgreSQL réel. Elle provisionne
/// deux schémas de tenants ISOLÉS (site_italpha, site_itbeta) et applique le
/// modèle EF Core dans chacun, pour permettre de vérifier que le cloisonnement
/// multi-tenant (TenantSchemaConnectionInterceptor) empêche réellement un site
/// de voir les données d'un autre — REQ-F-10, zone sensible CLAUDE.md §7.3.
///
/// Chaîne de connexion : variable d'environnement NOVACCES_TEST_POSTGRES, ou à
/// défaut la base de dev locale. Si aucun PostgreSQL n'est joignable, les tests
/// qui en dépendent sont SKIPPÉS (et non échoués), pour ne pas casser un CI
/// dépourvu de base.
/// </summary>
public sealed class PostgresTenantFixture : IDisposable
{
    public const string TenantA = "italpha";
    public const string TenantB = "itbeta";

    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=novacces;Username=postgres;Password=root";

    public string ConnectionString { get; }
    public bool DatabaseAvailable { get; }
    public string? SkipReason { get; }

    public PostgresTenantFixture()
    {
        ConnectionString =
            Environment.GetEnvironmentVariable("NOVACCES_TEST_POSTGRES") ?? DefaultConnectionString;

        try
        {
            using (var probe = new NpgsqlConnection(ConnectionString))
                probe.Open();

            ProvisionTenantSchema(TenantA);
            ProvisionTenantSchema(TenantB);

            DatabaseAvailable = true;
        }
        catch (Exception ex)
        {
            DatabaseAvailable = false;
            SkipReason = $"PostgreSQL non joignable ({ex.GetType().Name}: {ex.Message}). " +
                         "Définir NOVACCES_TEST_POSTGRES pour exécuter ces tests d'intégration.";
        }
    }

    /// <summary>
    /// Construit un DbContext cantonné au tenant donné, EXACTEMENT comme en
    /// production : même intercepteur de schéma, même CurrentTenant. C'est donc
    /// le vrai mécanisme de cloisonnement qui est testé, pas une simulation.
    /// </summary>
    public NovAccesDbContext CreateContext(string siteId)
    {
        var tenant = new CurrentTenant();
        tenant.Resolve(siteId);

        var options = new DbContextOptionsBuilder<NovAccesDbContext>()
            .UseNpgsql(ConnectionString)
            .AddInterceptors(new TenantSchemaConnectionInterceptor(tenant))
            .Options;

        return new NovAccesDbContext(options, tenant);
    }

    private void ProvisionTenantSchema(string siteId)
    {
        var schema = $"site_{siteId}";

        // Repartir d'un schéma vierge à chaque exécution pour un test déterministe...
        ExecuteRaw($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");

        // ...puis provisionner via le VRAI service de production (schéma + modèle
        // + journal append-only). La fixture teste donc aussi le provisionnement.
        new TenantProvisioningService(ConnectionString)
            .ProvisionAsync(siteId).GetAwaiter().GetResult();
    }

    private void ExecuteRaw(string sql)
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (!DatabaseAvailable)
            return;

        // Nettoyage : ne jamais laisser traîner les schémas de test.
        try
        {
            ExecuteRaw(
                $"DROP SCHEMA IF EXISTS \"site_{TenantA}\" CASCADE; " +
                $"DROP SCHEMA IF EXISTS \"site_{TenantB}\" CASCADE;");
        }
        catch
        {
            // Best-effort : un échec de nettoyage ne doit pas masquer le résultat des tests.
        }
    }
}
