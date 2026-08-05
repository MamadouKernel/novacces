using Microsoft.Extensions.Configuration;
using Npgsql;
using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure.Persistence.Tenancy;

namespace NovAcces.Infrastructure.Persistence;

/// <summary>
/// Diagnostic transverse en lecture seule : taille, connexions actives,
/// version, et statistiques par schéma (identity + chaque site). N'utilise
/// que des vues catalogue PostgreSQL (pg_stat_*, pg_namespace…), jamais les
/// données métier elles-mêmes — aucune donnée personnelle ne transite ici.
/// </summary>
public sealed class PostgresDatabaseHealthService : IDatabaseHealthService
{
    private readonly string _connectionString;

    public PostgresDatabaseHealthService(IConfiguration configuration) =>
        _connectionString = PostgresAdminConnectionResolver.Resolve(configuration);

    public async Task<DatabaseHealthOverview> GetOverviewAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var version = await ScalarAsync(connection, "SELECT version()", "", ct);
        var totalSize = await ScalarAsync(connection, "SELECT pg_database_size(current_database())", 0L, ct);
        var connections = await ScalarAsync(
            connection, "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()", 0L, ct);

        var schemas = new List<DatabaseSchemaStats>();
        await using (var command = connection.CreateCommand())
        {
            // Schémas de site ("site_<id>") + le schéma partagé "identity" —
            // jamais "public" (délibérément vide, cloisonnement §7.3) ni les
            // schémas système PostgreSQL.
            command.CommandText = """
                SELECT
                    n.nspname AS schema_name,
                    COALESCE(SUM(pg_total_relation_size(c.oid)), 0) AS size_bytes,
                    COUNT(c.oid) AS table_count,
                    COALESCE(SUM(s.n_live_tup), 0) AS approx_rows
                FROM pg_namespace n
                LEFT JOIN pg_class c ON c.relnamespace = n.oid AND c.relkind = 'r'
                LEFT JOIN pg_stat_user_tables s ON s.relid = c.oid
                WHERE n.nspname = 'identity' OR n.nspname LIKE 'site\_%'
                GROUP BY n.nspname
                ORDER BY n.nspname;
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                schemas.Add(new DatabaseSchemaStats(
                    reader.GetString(0), reader.GetInt64(1), (int)reader.GetInt64(2), reader.GetInt64(3)));
            }
        }

        return new DatabaseHealthOverview(version, totalSize, (int)connections, schemas);
    }

    // T? générique non contraint ne se comporte PAS comme Nullable<T> pour un
    // type valeur (piège C# : sans contrainte "struct", "T?" reste "T" en
    // sortie) — un paramètre defaultValue explicite évite l'ambiguïté plutôt
    // que de s'appuyer sur "?? 0", qui ne compile pas dans ce cas précis
    // (découvert par le build Release propre, avant de répéter l'incident
    // du 05/08/2026 sur AdminDatabase.razor).
    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, T defaultValue, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? defaultValue : (T)result;
    }
}
