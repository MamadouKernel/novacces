using Microsoft.Extensions.Configuration;
using Npgsql;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Énumère les sites provisionnés en listant les schémas PostgreSQL « site_* ».
/// Requête transverse (hors tenant) sur une connexion brute.
///
/// <see cref="ExistsAsync"/> est appelé sur le chemin de CHAQUE requête HTTP
/// (résolution de tenant) : le résultat est donc mis en cache quelques secondes.
/// Le provisionnement d'un site est une opération rare et manuelle ; un nouveau
/// site devient utilisable au plus tard après <see cref="CacheTtl"/>.
/// </summary>
public sealed class SiteCatalog : ISiteCatalog
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly string _connectionString;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private HashSet<string> _cachedSites = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public SiteCatalog(IConfiguration configuration) =>
        _connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Chaîne de connexion 'Postgres' manquante.");

    /// <summary>
    /// Le site est-il réellement provisionné ? Sans ce contrôle, un identifiant
    /// bien formé mais inexistant passait la validation, positionnait le
    /// search_path sur un schéma absent, et la requête retombait sur « public »
    /// pour finir en erreur PostgreSQL brute (500). On répond désormais 404.
    /// </summary>
    public async Task<bool> ExistsAsync(string siteId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(siteId)) return false;

        var sites = await GetCachedSitesAsync(ct);
        return sites.Contains(siteId);
    }

    public void Invalidate() => _cachedAt = DateTimeOffset.MinValue;

    private async Task<HashSet<string>> GetCachedSitesAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
            return _cachedSites;

        await _refreshLock.WaitAsync(ct);
        try
        {
            // Re-test après acquisition : une requête concurrente a pu rafraîchir.
            if (DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
                return _cachedSites;

            var sites = await QuerySiteIdsAsync(ct);
            _cachedSites = sites.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _cachedAt = DateTimeOffset.UtcNow;
            return _cachedSites;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Lecture NON mise en cache. Les appelants qui viennent de provisionner un
    /// site (création de compte, enrôlement de terminal) doivent voir la vérité
    /// immédiatement, pas dans 30 secondes.
    /// </summary>
    public Task<IReadOnlyList<string>> GetSiteIdsAsync(CancellationToken ct) => QuerySiteIdsAsync(ct);

    private async Task<IReadOnlyList<string>> QuerySiteIdsAsync(CancellationToken ct)
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
