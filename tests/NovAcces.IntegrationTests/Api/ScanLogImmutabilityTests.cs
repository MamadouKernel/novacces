using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;
using NovAcces.Infrastructure.Persistence;
using NovAcces.Infrastructure.Persistence.Tenancy;
using NovAcces.Infrastructure.Retention;
using Xunit;

namespace NovAcces.IntegrationTests.Api;

/// <summary>
/// Immuabilité du journal des scans (§7.5/8.5) et son UNIQUE exception RGPD :
/// au niveau base, seule l'anonymisation du nom du visiteur (nom → sentinel)
/// est permise. Toute autre modification — changer une colonne de sécurité,
/// renommer vers une valeur arbitraire, supprimer une ligne — est rejetée par
/// le trigger append-only, quel que soit le rôle SQL.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ScanLogImmutabilityTests
{
    private readonly NovAccesApiFactory _factory;

    public ScanLogImmutabilityTests(NovAccesApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task ScanLog_AllowsOnlyNameAnonymization_AndBlocksEverythingElse()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var id = await InsertScanLogAsync($"Nom Réel {Guid.NewGuid():N}", DateTimeOffset.UtcNow.AddDays(-5));
        var table = $"\"site_{NovAccesApiFactory.TestSite}\".scan_logs";
        var idLiteral = $"'{id}'";

        // Bloqué : altérer un fait de sécurité (autre colonne que le nom).
        await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync($"UPDATE {table} SET \"WasGranted\" = NOT \"WasGranted\" WHERE \"Id\" = {idLiteral}"));

        // Bloqué : renommer vers une valeur arbitraire (≠ sentinel).
        await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync($"UPDATE {table} SET \"VisitorName\" = 'Autre Nom' WHERE \"Id\" = {idLiteral}"));

        // Bloqué : suppression.
        await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync($"DELETE FROM {table} WHERE \"Id\" = {idLiteral}"));

        // Autorisé : anonymisation du nom (et rien d'autre).
        await ExecuteAsync($"UPDATE {table} SET \"VisitorName\" = '{RetentionOptions.AnonymizationSentinel}' WHERE \"Id\" = {idLiteral}");
        Assert.Equal(RetentionOptions.AnonymizationSentinel, await ReadNameAsync(id));
    }

    // ---- Aides ----

    private async Task<Guid> InsertScanLogAsync(string visitorName, DateTimeOffset when)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentTenant>().Resolve(NovAccesApiFactory.TestSite);
        var db = scope.ServiceProvider.GetRequiredService<NovAccesDbContext>();

        var entry = ScanLogEntry.Create(
            Guid.NewGuid(), visitorName, "agent-test", CheckpointDirection.Entry,
            ScanOutcome.Granted(), degradedMode: false, "Test immuabilité", when);
        db.ScanLogs.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(NovAccesApiFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadNameAsync(Guid scanLogId)
    {
        await using var conn = new NpgsqlConnection(NovAccesApiFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT \"VisitorName\" FROM \"site_{NovAccesApiFactory.TestSite}\".scan_logs WHERE \"Id\" = @id";
        cmd.Parameters.AddWithValue("id", scanLogId);
        return (string?)await cmd.ExecuteScalarAsync();
    }
}
