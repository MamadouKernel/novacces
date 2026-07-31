using Npgsql;
using Xunit;

namespace NovAcces.IntegrationTests;

/// <summary>
/// Vérifie le résultat du provisionnement d'un site (TenantProvisioningService),
/// en particulier l'inaltérabilité du journal des scans exigée par CLAUDE.md §7.4
/// et REQ-SEC-05 : une fois écrit, un enregistrement de scan ne doit pouvoir être
/// ni modifié, ni supprimé, ni tronqué — même par un rôle privilégié.
///
/// Le schéma est provisionné par la fixture (qui utilise le vrai service).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TenantProvisioningTests
{
    private readonly PostgresTenantFixture _fixture;

    public TenantProvisioningTests(PostgresTenantFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public void ProvisionedSchema_HasExpectedTables()
    {
        Skip.IfNot(_fixture.DatabaseAvailable, _fixture.SkipReason);

        var tables = QueryTableNames($"site_{PostgresTenantFixture.TenantA}");

        Assert.Contains("visits", tables);
        Assert.Contains("scan_logs", tables);
    }

    [SkippableFact]
    public void ScanLogsJournal_IsAppendOnly_UpdateAndDeleteAreRejected()
    {
        Skip.IfNot(_fixture.DatabaseAvailable, _fixture.SkipReason);

        var schema = $"site_{PostgresTenantFixture.TenantA}";
        using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        connection.Open();
        SetSearchPath(connection, schema);

        // INSERT : autorisé (fonctionnement nominal du journal).
        var logId = Guid.NewGuid();
        Execute(connection, $"""
            INSERT INTO scan_logs
              ("Id","VisitId","VisitorName","AgentId","Direction","WasGranted","WasCheckOut",
               "IsSecurityEvent","RecordedInDegradedMode","Detail","Timestamp")
            VALUES
              ('{logId}','{Guid.NewGuid()}','Test','agent-it',0,true,false,false,false,'insert ok', now());
            """);

        // UPDATE : doit être rejeté par le trigger append-only.
        var updateEx = Assert.Throws<PostgresException>(() =>
            Execute(connection, $"UPDATE scan_logs SET \"Detail\" = 'falsifié' WHERE \"Id\" = '{logId}';"));
        Assert.Contains("append-only", updateEx.MessageText);

        // DELETE : doit également être rejeté.
        var deleteEx = Assert.Throws<PostgresException>(() =>
            Execute(connection, $"DELETE FROM scan_logs WHERE \"Id\" = '{logId}';"));
        Assert.Contains("append-only", deleteEx.MessageText);

        // La ligne insérée est toujours là, intacte.
        using var check = connection.CreateCommand();
        check.CommandText = $"SELECT \"Detail\" FROM scan_logs WHERE \"Id\" = '{logId}';";
        Assert.Equal("insert ok", (string?)check.ExecuteScalar());
    }

    private List<string> QueryTableNames(string schema)
    {
        using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT tablename FROM pg_tables WHERE schemaname = @schema";
        command.Parameters.AddWithValue("schema", schema);

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static void SetSearchPath(NpgsqlConnection connection, string schema)
        => Execute(connection, $"SET search_path TO \"{schema}\"");

    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
