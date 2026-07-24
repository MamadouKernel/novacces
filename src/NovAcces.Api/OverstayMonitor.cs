using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure.Overstay;
using Microsoft.Extensions.Options;

namespace NovAcces.Api;

/// <summary>
/// Service de fond qui déclenche périodiquement la supervision des dépassements
/// (§7). Ne porte aucune logique métier : il rythme les appels à IOverstayScanner.
/// </summary>
public sealed class OverstayMonitor : BackgroundService
{
    private readonly IOverstayScanner _scanner;
    private readonly OverstayOptions _options;
    private readonly ILogger<OverstayMonitor> _logger;

    public OverstayMonitor(
        IOverstayScanner scanner,
        IOptions<OverstayOptions> options,
        ILogger<OverstayMonitor> logger)
    {
        _scanner = scanner;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Supervision des dépassements désactivée (Overstay:Enabled=false).");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(15, _options.ScanIntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        try
        {
            // On attend un premier intervalle avant le premier passage : laisse
            // l'application démarrer, et évite de muter l'état pendant les tests
            // d'intégration (qui pilotent le scanner explicitement).
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await _scanner.ScanOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Passage de supervision des dépassements en échec.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Arrêt normal de l'application.
        }
    }
}
