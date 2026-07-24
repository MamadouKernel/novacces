using Microsoft.Extensions.Configuration;
using Npgsql;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Énumère les sites provisionnés en listant les schémas PostgreSQL « site_* ».
/// Requête transverse (hors tenant) sur une connexion brute.
/// </summary>
public sealed class SiteCatalog : ISiteCatalog
{
    private readonly string _connectionString;

    public SiteCatalog(IConfiguration configuration) =>
        _connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Chaîne de connexion 'Postgres' manquante.");

    public async Task<IReadOnlyList<string>> GetSiteIdsAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT schema_name FROM information_schema.schemata WHERE schema_name LIKE 'site\\_%'";

        var sites = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var schema = reader.GetString(0);
            sites.Add(schema["site_".Length..]); // "site_sicopa" -> "sicopa"
        }

        return sites;
    }
}
