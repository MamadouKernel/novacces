using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure.Retention;
using Microsoft.Extensions.Options;

namespace NovAcces.Api;

/// <summary>
/// Service de fond qui déclenche périodiquement la purge des données au-delà de
/// la durée de conservation (section 7.3). Ne porte aucune logique métier : il
/// rythme les appels à IDataRetentionService.
/// </summary>
public sealed class RetentionMonitor : BackgroundService
{
    private readonly IDataRetentionService _retention;
    private readonly RetentionOptions _options;
    private readonly ILogger<RetentionMonitor> _logger;

    public RetentionMonitor(
        IDataRetentionService retention,
        IOptions<RetentionOptions> options,
        ILogger<RetentionMonitor> logger)
    {
        _retention = retention;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Purge de rétention désactivée (Retention:Enabled=false).");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, _options.RunIntervalHours));
        using var timer = new PeriodicTimer(interval);

        try
        {
            // Premier passage après un intervalle : laisse l'application démarrer
            // et évite de muter l'état pendant les tests d'intégration (qui
            // pilotent le service explicitement).
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var results = await _retention.PurgeOnceAsync(stoppingToken);
                    var total = results.Sum(r => r.VisitsPurged);
                    if (total > 0)
                        _logger.LogInformation("Purge de rétention : {Total} demande(s) supprimée(s) sur {Sites} site(s).",
                            total, results.Count);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Passage de purge de rétention en échec.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Arrêt normal de l'application.
        }
    }
}
