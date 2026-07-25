using Npgsql;

namespace NovAcces.IntegrationTests;

/// <summary>
/// Base de données DÉDIÉE aux tests d'intégration — jamais la base de
/// développement « novacces », pour ne pas la polluer avec des données de
/// test. Créée automatiquement si absente. Surchargeable via la variable
/// d'environnement NOVACCES_TEST_POSTGRES (ex. en CI).
/// </summary>
internal static class TestDatabase
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=novacces_test;Username=postgres;Password=root";

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("NOVACCES_TEST_POSTGRES") ?? DefaultConnectionString;

    /// <summary>Crée la base de test si elle n'existe pas encore (idempotent).</summary>
    public static void EnsureCreated()
    {
        var target = new NpgsqlConnectionStringBuilder(ConnectionString);
        var dbName = target.Database!;

        // Connexion à la base de maintenance « postgres » (mêmes hôte/identifiants)
        // pour pouvoir créer la base cible : un CREATE DATABASE ne peut se faire
        // depuis la base que l'on crée.
        var maintenance = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres" };

        using var conn = new NpgsqlConnection(maintenance.ConnectionString);
        conn.Open();

        using (var check = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", conn))
        {
            check.Parameters.AddWithValue("n", dbName);
            if (check.ExecuteScalar() is not null) return;
        }

        using var create = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", conn);
        create.ExecuteNonQuery();
    }
}
