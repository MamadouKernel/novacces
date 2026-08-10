using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NovAcces.Infrastructure.Persistence;
using Xunit;

namespace NovAcces.IntegrationTests.Api;

/// <summary>
/// Preuve empirique (pas seulement documentaire) que la sauvegarde/restauration
/// complète fonctionne de bout en bout, chiffrement au repos compris — le
/// gap identifié dans la revue du 10/08/2026 (§7.4/REQ-FIAB-03) était
/// précisément l'absence d'un test de restauration réellement exécuté.
///
/// Opère sur une base JETABLE dédiée (créée puis supprimée par le test),
/// jamais sur `novacces_test` : un `pg_restore --clean` contre une base dont
/// le pool de connexions du reste de la suite est actif serait instable et
/// non représentatif (en production, la restauration est délibérément
/// CLI-only avec l'API arrêtée — voir IDatabaseBackupService).
/// </summary>
public sealed class DatabaseBackupRestoreTests : IAsyncLifetime
{
    private readonly string _dbName = "novacces_bkp_" + Guid.NewGuid().ToString("N")[..12];
    private string _maintenanceConnectionString = "";
    private string _targetConnectionString = "";
    private string _backupDirectory = "";

    public async Task InitializeAsync()
    {
        if (!IsPostgresAvailable())
            return;

        var baseCsb = new NpgsqlConnectionStringBuilder(TestDatabase.ConnectionString);
        _maintenanceConnectionString = new NpgsqlConnectionStringBuilder(baseCsb.ConnectionString) { Database = "postgres" }.ConnectionString;
        _targetConnectionString = new NpgsqlConnectionStringBuilder(baseCsb.ConnectionString) { Database = _dbName }.ConnectionString;

        await using var conn = new NpgsqlConnection(_maintenanceConnectionString);
        await conn.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_dbName}\"", conn);
        await create.ExecuteNonQueryAsync();

        _backupDirectory = Path.Combine(Path.GetTempPath(), "novacces-backup-test-" + Guid.NewGuid().ToString("N"));
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (!string.IsNullOrEmpty(_backupDirectory) && Directory.Exists(_backupDirectory))
                Directory.Delete(_backupDirectory, recursive: true);
        }
        catch { /* best-effort */ }

        if (string.IsNullOrEmpty(_maintenanceConnectionString))
            return;

        try
        {
            await using var conn = new NpgsqlConnection(_maintenanceConnectionString);
            await conn.OpenAsync();

            await using (var terminate = new NpgsqlCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @n AND pid <> pg_backend_pid()", conn))
            {
                terminate.Parameters.AddWithValue("n", _dbName);
                await terminate.ExecuteNonQueryAsync();
            }

            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_dbName}\"", conn);
            await drop.ExecuteNonQueryAsync();
        }
        catch { /* best-effort */ }
    }

    [SkippableFact]
    public async Task Backup_ThenRestore_RecoversDataExactly_AndBackupIsEncrypted()
    {
        Skip.IfNot(IsPostgresAvailable(), "PostgreSQL indisponible dans cet environnement.");

        // 1. Seed d'une donnée témoin dans la base jetable.
        await using (var seedConn = new NpgsqlConnection(_targetConnectionString))
        {
            await seedConn.OpenAsync();
            await using var createTable = new NpgsqlCommand(
                "CREATE TABLE temoin (id integer PRIMARY KEY, valeur text NOT NULL)", seedConn);
            await createTable.ExecuteNonQueryAsync();
            await using var insert = new NpgsqlCommand(
                "INSERT INTO temoin (id, valeur) VALUES (1, 'donnee-avant-sauvegarde')", seedConn);
            await insert.ExecuteNonQueryAsync();
        }

        const string passphrase = "phrase-de-test-suffisamment-longue-pour-pbkdf2";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _targetConnectionString,
            })
            .Build();
        var options = Options.Create(new DatabaseBackupOptions
        {
            Directory = _backupDirectory,
            EncryptionPassphrase = passphrase,
        });
        var offsite = new S3BackupOffsiteUploader(
            Options.Create(new OffsiteBackupOptions { Enabled = false }),
            NullLogger<S3BackupOffsiteUploader>.Instance);
        var backupService = new PgDumpDatabaseBackupService(config, options, offsite, NullLogger<PgDumpDatabaseBackupService>.Instance);

        // 2. Sauvegarde réelle (pg_dump + chiffrement AES-256-GCM).
        var info = await backupService.CreateBackupAsync(CancellationToken.None);
        Assert.EndsWith(".dump.enc", info.FileName);

        var onDisk = Directory.GetFiles(_backupDirectory);
        Assert.Single(onDisk);
        // Le fichier sur disque ne doit contenir NULLE PART le texte en clair
        // du témoin — preuve directe que le chiffrement s'applique bien au
        // contenu réel du dump, pas seulement à l'extension du fichier.
        var rawBytes = await File.ReadAllBytesAsync(onDisk[0]);
        var plaintextNeedle = System.Text.Encoding.UTF8.GetBytes("donnee-avant-sauvegarde");
        Assert.True(
            rawBytes.AsSpan().IndexOf(plaintextNeedle) < 0,
            "Le fichier de sauvegarde contient la donnée témoin en clair — le chiffrement n'a pas été appliqué.");

        // 3. Mutation destructive après la sauvegarde — simule une perte de données.
        await using (var mutateConn = new NpgsqlConnection(_targetConnectionString))
        {
            await mutateConn.OpenAsync();
            await using var truncate = new NpgsqlCommand("DROP TABLE temoin", mutateConn);
            await truncate.ExecuteNonQueryAsync();
        }

        // 4. Restauration réelle (déchiffrement + pg_restore --clean).
        await backupService.RestoreBackupAsync(info.FileName, CancellationToken.None);

        // 5. La donnée témoin doit être revenue EXACTEMENT.
        await using var verifyConn = new NpgsqlConnection(_targetConnectionString);
        await verifyConn.OpenAsync();
        await using var select = new NpgsqlCommand("SELECT valeur FROM temoin WHERE id = 1", verifyConn);
        var restored = await select.ExecuteScalarAsync();
        Assert.Equal("donnee-avant-sauvegarde", restored);

        // Le fichier de sauvegarde temporaire de restauration ne doit jamais
        // survivre (nettoyage du plaintext déchiffré, voir RestoreBackupAsync finally).
        Assert.DoesNotContain(Directory.GetFiles(_backupDirectory), f => f.EndsWith(".restoretmp"));
    }

    /// <summary>
    /// Sonde légère, sans passer par NovAccesApiFactory (qui démarre tout
    /// l'hôte web + migrations + seed — inutile pour ce test isolé).
    /// </summary>
    private static bool IsPostgresAvailable()
    {
        try
        {
            TestDatabase.EnsureCreated();
            using var probe = new NpgsqlConnection(TestDatabase.ConnectionString);
            probe.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
