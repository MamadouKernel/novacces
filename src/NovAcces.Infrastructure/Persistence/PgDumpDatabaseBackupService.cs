using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure.Persistence.Tenancy;

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

    /// <summary>
    /// Passphrase de chiffrement AES-256-GCM au repos (§7.4, voir
    /// BackupEncryption). Vide = sauvegarde en clair — toléré en
    /// développement uniquement ; la production l'exige (voir
    /// ProductionConfigurationValidator). Jamais journalisée.
    /// </summary>
    public string EncryptionPassphrase { get; set; } = "";

    /// <summary>Planification automatique (voir BackupScheduler) — désactivée par défaut.</summary>
    public AutoBackupOptions AutoBackup { get; set; } = new();
}

public sealed class AutoBackupOptions
{
    public bool Enabled { get; set; }
    public int IntervalHours { get; set; } = 24;
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
        new(@"^novacces_\d{8}_\d{6}\.dump(\.enc)?$", RegexOptions.Compiled);

    private readonly string _connectionString;
    private readonly string _directory;
    private readonly int _maxBackupsToKeep;
    private readonly string _encryptionPassphrase;
    private readonly IBackupOffsiteUploader _offsite;
    private readonly ILogger<PgDumpDatabaseBackupService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PgDumpDatabaseBackupService(
        IConfiguration configuration,
        IOptions<DatabaseBackupOptions> options,
        IBackupOffsiteUploader offsite,
        ILogger<PgDumpDatabaseBackupService> logger)
    {
        // Connexion PROPRIÉTAIRE si configurée : une sauvegarde/restauration
        // complète bénéficie de la visibilité et des droits DDL les plus
        // larges, pas seulement ceux du rôle applicatif restreint (la
        // restauration en particulier a besoin de pouvoir DROP/CREATE).
        _connectionString = PostgresAdminConnectionResolver.Resolve(configuration);

        var opts = options.Value;
        _directory = string.IsNullOrWhiteSpace(opts.Directory)
            ? Path.Combine(AppContext.BaseDirectory, "backups")
            : opts.Directory;
        _maxBackupsToKeep = opts.MaxBackupsToKeep <= 0 ? 14 : opts.MaxBackupsToKeep;
        _encryptionPassphrase = opts.EncryptionPassphrase;
        _offsite = offsite;
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

            var (exitCode, stderr) = await RunPgToolAsync(
                "pg_dump",
                new[] { "-Fc", "--no-owner", "--no-privileges", "-f", path },
                ct);

            if (exitCode != 0)
            {
                TryDelete(path);
                _logger.LogError("pg_dump a échoué (code {ExitCode}) : {Stderr}", exitCode, stderr);
                throw new DatabaseBackupFailedException(
                    $"pg_dump a échoué (code {exitCode}). Voir les logs serveur pour le détail.");
            }

            var finalPath = path;
            var finalFileName = fileName;
            if (!string.IsNullOrEmpty(_encryptionPassphrase))
            {
                var encryptedPath = path + ".enc";
                BackupEncryption.EncryptFile(path, encryptedPath, _encryptionPassphrase);
                TryDelete(path);
                finalPath = encryptedPath;
                finalFileName = fileName + ".enc";
            }
            else
            {
                _logger.LogWarning(
                    "Sauvegarde créée SANS chiffrement (DatabaseBackup:EncryptionPassphrase non configurée) — "
                    + "acceptable en développement uniquement, cf. §7.4.");
            }

            var info = new FileInfo(finalPath);
            _logger.LogInformation(
                "Sauvegarde de base créée : {FileName} ({SizeBytes} octets).", finalFileName, info.Length);

            await PruneOldBackupsAsync(ct);

            try
            {
                await _offsite.UploadAsync(finalPath, finalFileName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Échec de la réplication hors-site de la sauvegarde (best-effort) — la copie locale reste valide.");
            }

            return new DatabaseBackupInfo(finalFileName, info.Length, DateTimeOffset.UtcNow);
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

    /// <summary>
    /// Restaure la base COMPLÈTE depuis une sauvegarde existante. ÉCRASE
    /// (--clean --if-exists) tous les objets courants avant de les recréer
    /// tels qu'ils étaient au moment de la sauvegarde — y compris les
    /// triggers d'inaltérabilité des journaux, qui font partie du schéma
    /// restauré. Nécessite la connexion PROPRIÉTAIRE (DROP/CREATE), jamais
    /// le rôle applicatif restreint. Voir la remarque de classe de
    /// IDatabaseBackupService : volontairement CLI uniquement.
    /// </summary>
    public async Task RestoreBackupAsync(string fileName, CancellationToken ct)
    {
        if (!IsValidBackupFileName(fileName))
            throw new DatabaseBackupFailedException($"Nom de sauvegarde invalide : « {fileName} ».");

        var path = Path.Combine(_directory, fileName);
        if (!File.Exists(path))
            throw new DatabaseBackupFailedException($"Sauvegarde introuvable : « {fileName} ».");

        if (!await _gate.WaitAsync(0, ct))
            throw new DatabaseBackupInProgressException();

        var restorePath = path;
        var decryptedTempPath = fileName.EndsWith(".enc", StringComparison.OrdinalIgnoreCase) ? path + ".restoretmp" : null;

        try
        {
            if (decryptedTempPath is not null)
            {
                if (string.IsNullOrEmpty(_encryptionPassphrase))
                    throw new DatabaseBackupFailedException(
                        "Sauvegarde chiffrée mais DatabaseBackup:EncryptionPassphrase non configurée — restauration impossible.");

                BackupEncryption.DecryptFile(path, decryptedTempPath, _encryptionPassphrase);
                restorePath = decryptedTempPath;
            }

            _logger.LogWarning("Restauration de base démarrée depuis {FileName} — écrase les données courantes.", fileName);

            var (exitCode, stderr) = await RunPgToolAsync(
                "pg_restore",
                new[] { "--clean", "--if-exists", "--no-owner", "--no-privileges", restorePath },
                ct);

            // pg_restore retourne parfois un code non nul pour de simples
            // avertissements (ex. objet absent lors du --if-exists sur une
            // base vide) : on journalise systématiquement stderr, mais on ne
            // considère l'opération en échec QUE sur un code de sortie fatal.
            if (exitCode != 0)
            {
                _logger.LogError("pg_restore a terminé avec des erreurs (code {ExitCode}) : {Stderr}", exitCode, stderr);
                throw new DatabaseBackupFailedException(
                    $"pg_restore a échoué (code {exitCode}). Voir les logs serveur pour le détail — la base peut être dans un état partiel.");
            }

            _logger.LogWarning("Restauration de base terminée depuis {FileName}.", fileName);
        }
        finally
        {
            if (decryptedTempPath is not null)
                TryDelete(decryptedTempPath);
            _gate.Release();
        }
    }

    /// <summary>
    /// Lance pg_dump/pg_restore avec les paramètres de connexion communs
    /// (-h/-p/-U/-d puis PGPASSWORD en variable d'environnement, jamais en
    /// ligne de commande — voir CreateBackupAsync). <paramref name="extraArgs"/>
    /// contient les options propres à l'outil (format, fichier, --clean…).
    /// </summary>
    private async Task<(int ExitCode, string Stderr)> RunPgToolAsync(
        string tool, IReadOnlyList<string> extraArgs, CancellationToken ct)
    {
        var csb = new NpgsqlConnectionStringBuilder(_connectionString);

        var psi = new ProcessStartInfo
        {
            FileName = tool,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-h"); psi.ArgumentList.Add(csb.Host ?? "localhost");
        psi.ArgumentList.Add("-p"); psi.ArgumentList.Add((csb.Port == 0 ? 5432 : csb.Port).ToString());
        psi.ArgumentList.Add("-U"); psi.ArgumentList.Add(csb.Username ?? "");
        psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(csb.Database ?? "");
        foreach (var arg in extraArgs)
            psi.ArgumentList.Add(arg);

        psi.Environment["PGPASSWORD"] = csb.Password ?? "";

        using var process = Process.Start(psi)
            ?? throw new DatabaseBackupFailedException($"Impossible de démarrer {tool}.");

        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stderr = await stderrTask;

        return (process.ExitCode, stderr);
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
