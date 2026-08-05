using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Persistence;

public sealed class DatabaseBackupOptions
{
    /// <summary>
    /// Dossier des sauvegardes. Vide = repli sur "{BaseDirectory}/backups"
    /// (dev). En production, correspond au volume Docker nommé "backups"
    /// monté sur /app/backups (voir docker-compose.yml) — sans ce volume,
    /// une sauvegarde disparaîtrait à la prochaine recréation du conteneur.
    /// </summary>
    public string Directory { get; set; } = "";

    /// <summary>
    /// Nombre de sauvegardes conservées ; les plus anciennes au-delà sont
    /// supprimées après chaque sauvegarde réussie (le disque du VPS n'est
    /// pas extensible — une purge non bornée finirait par saturer le volume
    /// et mettre l'application entière hors service).
    /// </summary>
    public int MaxBackupsToKeep { get; set; } = 14;
}

/// <summary>
/// Sauvegarde de la base COMPLÈTE via pg_dump (format personnalisé,
/// compressé, restaurable par pg_restore). Nécessite le client pg_dump dans
/// l'image (voir NovAcces.Api/Dockerfile — postgresql-client-16, aligné sur
/// la version du serveur "postgres:16-alpine" du docker-compose).
///
/// ZONE SENSIBLE : une sauvegarde complète contient les données de TOUS les
/// clients de Sigasécurité en un seul fichier — réservée au SuperAdmin côté
/// endpoints (DatabaseAdminEndpoints.cs), jamais journalisée en clair (le mot
/// de passe transite par la variable d'environnement PGPASSWORD du
/// sous-processus, jamais en ligne de commande ni dans les logs).
/// </summary>
public sealed class PgDumpDatabaseBackupService : IDatabaseBackupService
{
    private static readonly Regex FileNamePattern =
        new(@"^novacces_\d{8}_\d{6}\.dump$", RegexOptions.Compiled);

    private readonly string _connectionString;
    private readonly string _directory;
    private readonly int _maxBackupsToKeep;
    private readonly ILogger<PgDumpDatabaseBackupService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PgDumpDatabaseBackupService(
        IConfiguration configuration,
        IOptions<DatabaseBackupOptions> options,
        ILogger<PgDumpDatabaseBackupService> logger)
    {
        // Connexion PROPRIÉTAIRE si configurée (même repli que
        // TenantProvisioningService) : une sauvegarde complète bénéficie de
        // la visibilité la plus large sur le schéma, pas seulement celle du
        // rôle applicatif restreint.
        _connectionString = configuration.GetConnectionString("PostgresOwner") ?? "";
        if (string.IsNullOrWhiteSpace(_connectionString))
            _connectionString = configuration.GetConnectionString("Postgres")
                ?? throw new InvalidOperationException("Chaîne de connexion 'Postgres' manquante.");

        var opts = options.Value;
        _directory = string.IsNullOrWhiteSpace(opts.Directory)
            ? Path.Combine(AppContext.BaseDirectory, "backups")
            : opts.Directory;
        _maxBackupsToKeep = opts.MaxBackupsToKeep <= 0 ? 14 : opts.MaxBackupsToKeep;
        _logger = logger;
    }

    /// <summary>
    /// Validation stricte d'un nom de fichier de sauvegarde — SEULE porte
    /// d'entrée acceptée pour construire un chemin disque à partir d'une
    /// valeur venue de l'appelant (téléchargement). Un nom qui ne correspond
    /// pas exactement au format généré ici est refusé, ce qui élimine toute
    /// traversée de chemin ("../", chemin absolu, etc.) par construction :
    /// il ne reste plus qu'à concaténer un composant de nom de fichier connu
    /// pour être plat (aucun séparateur possible dans le motif).
    /// </summary>
    public static bool IsValidBackupFileName(string? fileName) =>
        !string.IsNullOrEmpty(fileName) && FileNamePattern.IsMatch(fileName);

    public async Task<DatabaseBackupInfo> CreateBackupAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
            throw new DatabaseBackupInProgressException();

        try
        {
            System.IO.Directory.CreateDirectory(_directory);

            var fileName = $"novacces_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.dump";
            var path = Path.Combine(_directory, fileName);

            var csb = new NpgsqlConnectionStringBuilder(_connectionString);

            var psi = new ProcessStartInfo
            {
                FileName = "pg_dump",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-h"); psi.ArgumentList.Add(csb.Host ?? "localhost");
            psi.ArgumentList.Add("-p"); psi.ArgumentList.Add((csb.Port == 0 ? 5432 : csb.Port).ToString());
            psi.ArgumentList.Add("-U"); psi.ArgumentList.Add(csb.Username ?? "");
            psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(csb.Database ?? "");
            psi.ArgumentList.Add("-Fc"); // format personnalisé : compressé, restaurable sélectivement par pg_restore
            psi.ArgumentList.Add("--no-owner");
            psi.ArgumentList.Add("--no-privileges"); // évite des échecs de restauration si les rôles diffèrent à la cible
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(path);

            // Le mot de passe ne doit JAMAIS apparaître en ligne de commande
            // (visible via `ps` pour tout autre processus du conteneur) : il
            // passe exclusivement par la variable d'environnement standard
            // libpq, propre à ce sous-processus.
            psi.Environment["PGPASSWORD"] = csb.Password ?? "";

            using var process = Process.Start(psi)
                ?? throw new DatabaseBackupFailedException("Impossible de démarrer pg_dump.");

            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                TryDelete(path);
                _logger.LogError("pg_dump a échoué (code {ExitCode}) : {Stderr}", process.ExitCode, stderr);
                throw new DatabaseBackupFailedException(
                    $"pg_dump a échoué (code {process.ExitCode}). Voir les logs serveur pour le détail.");
            }

            var info = new FileInfo(path);
            _logger.LogInformation(
                "Sauvegarde de base créée : {FileName} ({SizeBytes} octets).", fileName, info.Length);

            await PruneOldBackupsAsync(ct);

            return new DatabaseBackupInfo(fileName, info.Length, DateTimeOffset.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<DatabaseBackupInfo>> ListBackupsAsync(CancellationToken ct)
    {
        if (!System.IO.Directory.Exists(_directory))
            return Task.FromResult<IReadOnlyList<DatabaseBackupInfo>>(Array.Empty<DatabaseBackupInfo>());

        var backups = System.IO.Directory.EnumerateFiles(_directory, "novacces_*.dump")
            .Select(p => new FileInfo(p))
            .Where(f => IsValidBackupFileName(f.Name))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new DatabaseBackupInfo(f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList();

        return Task.FromResult<IReadOnlyList<DatabaseBackupInfo>>(backups);
    }

    public Task<Stream?> OpenBackupForDownloadAsync(string fileName, CancellationToken ct)
    {
        if (!IsValidBackupFileName(fileName))
            return Task.FromResult<Stream?>(null);

        var path = Path.Combine(_directory, fileName);
        if (!File.Exists(path))
            return Task.FromResult<Stream?>(null);

        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult<Stream?>(stream);
    }

    private async Task PruneOldBackupsAsync(CancellationToken ct)
    {
        var backups = await ListBackupsAsync(ct);
        foreach (var stale in backups.Skip(_maxBackupsToKeep))
            TryDelete(Path.Combine(_directory, stale.FileName));
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "Impossible de supprimer {Path}.", path); }
    }
}
