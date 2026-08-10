using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure.Persistence;

namespace NovAcces.Api;

/// <summary>
/// Service de fond qui déclenche périodiquement une sauvegarde complète de la
/// base (§7.4/REQ-FIAB-03 — "sauvegardes quotidiennes automatiques"). Même
/// patron que RetentionMonitor/OverstayMonitor : ne porte aucune logique de
/// sauvegarde elle-même, rythme les appels à IDatabaseBackupService. Désactivé
/// par défaut (DatabaseBackup:AutoBackup:Enabled) — la production l'exige
/// (voir ProductionConfigurationValidator), le développement s'en passe.
/// </summary>
public sealed class BackupScheduler : BackgroundService
{
    private readonly IDatabaseBackupService _backups;
    private readonly AutoBackupOptions _options;
    private readonly ILogger<BackupScheduler> _logger;

    public BackupScheduler(
        IDatabaseBackupService backups,
        IOptions<DatabaseBackupOptions> options,
        ILogger<BackupScheduler> logger)
    {
        _backups = backups;
        _options = options.Value.AutoBackup;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Sauvegarde automatique désactivée (DatabaseBackup:AutoBackup:Enabled=false).");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, _options.IntervalHours));
        using var timer = new PeriodicTimer(interval);

        try
        {
            // Premier passage après un intervalle, même raisonnement que
            // RetentionMonitor : laisse l'application démarrer avant de
            // charger pg_dump, et évite de déclencher une sauvegarde pendant
            // les tests d'intégration qui pilotent le service explicitement.
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var info = await _backups.CreateBackupAsync(stoppingToken);
                    _logger.LogInformation(
                        "Sauvegarde automatique créée : {FileName} ({SizeBytes} octets).", info.FileName, info.SizeBytes);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Sauvegarde automatique en échec.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Arrêt normal de l'application.
        }
    }
}
