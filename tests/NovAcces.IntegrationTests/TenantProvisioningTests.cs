using Npgsql;
using NovAcces.Infrastructure.Persistence.Tenancy;
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

    [SkippableFact]
    public async Task ApplicationRole_CannotDeleteJournals_EvenIfTriggersWereDropped()
    {
        Skip.IfNot(_fixture.DatabaseAvailable, _fixture.SkipReason);

        // Ce test vérifie la SECONDE barrière : les privilèges PostgreSQL.
        // Les triggers restent la garantie principale, mais ils sont
        // supprimables lors d'une maintenance maladroite — un rôle applicatif
        // non propriétaire, lui, ne peut de toute façon pas effacer un journal.
        // Cette barrière n'a d'effet QUE parce que le rôle testé ici n'est pas
        // le propriétaire du schéma (un propriétaire garde des droits
        // implicites : le REVOKE ne l'atteindrait pas).
        const string appRole = "novacces_app_it";
        const string appPassword = "role-applicatif-de-test";
        var siteId = "roletest" + Guid.NewGuid().ToString("N")[..8];
        var schema = $"site_{siteId}";

        using (var owner = new NpgsqlConnection(_fixture.ConnectionString))
        {
            owner.Open();
            Execute(owner, $"""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{appRole}') THEN
                        CREATE ROLE {appRole} LOGIN PASSWORD '{appPassword}';
                    END IF;
                END
                $$;
                """);
        }

        // Provisionnement avec la connexion PROPRIÉTAIRE + le rôle applicatif.
        var provisioning = new TenantProvisioningService(_fixture.ConnectionString, appRole);
        await provisioning.ProvisionAsync(siteId);

        var appConnectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            Username = appRole,
            Password = appPassword,
        }.ConnectionString;

        using var app = new NpgsqlConnection(appConnectionString);
        app.Open();
        SetSearchPath(app, schema);

        // Le rôle applicatif écrit normalement dans le journal.
        var logId = Guid.NewGuid();
        Execute(app, $"""
            INSERT INTO scan_logs
              ("Id","VisitId","VisitorName","AgentId","Direction","WasGranted","WasCheckOut",
               "IsSecurityEvent","RecordedInDegradedMode","Detail","Timestamp")
            VALUES
              ('{logId}','{Guid.NewGuid()}','Test','agent-it',0,true,false,false,false,'insert ok', now());
            """);

        // Mais il n'a AUCUN droit de suppression — refus au niveau privilèges,
        // pas au niveau trigger (message PostgreSQL « permission denied »).
        var deleteEx = Assert.Throws<PostgresException>(() =>
            Execute(app, $"DELETE FROM scan_logs WHERE \"Id\" = '{logId}';"));
        Assert.Equal("42501", deleteEx.SqlState); // insufficient_privilege

        var truncateEx = Assert.Throws<PostgresException>(() => Execute(app, "TRUNCATE scan_logs;"));
        Assert.Equal("42501", truncateEx.SqlState);

        // admin_audit est verrouillé plus fort encore : aucune modification.
        var auditUpdateEx = Assert.Throws<PostgresException>(() =>
            Execute(app, "UPDATE admin_audit SET \"Detail\" = 'falsifié';"));
        Assert.Equal("42501", auditUpdateEx.SqlState);

        // En revanche l'anonymisation RGPD (§7.3) DOIT rester possible :
        // UPDATE n'est volontairement pas retiré sur scan_logs, c'est le
        // trigger qui le borne à la seule transition « nom → sentinel ».
        Execute(app, $"""
            UPDATE scan_logs SET "VisitorName" = '[anonymisé]' WHERE "Id" = '{logId}';
            """);

        using var check = app.CreateCommand();
        check.CommandText = $"SELECT \"VisitorName\" FROM scan_logs WHERE \"Id\" = '{logId}';";
        Assert.Equal("[anonymisé]", (string?)check.ExecuteScalar());
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
