using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure.Persistence;
using NovAcces.Infrastructure.Persistence.Tenancy;

namespace NovAcces.Infrastructure.Overstay;

/// <summary>
/// Paramètres de supervision des dépassements (§7). Intervalle entre rappels :
/// 2 min en démo, ~15 min en production (paramétrable par déploiement).
/// </summary>
public sealed class OverstayOptions
{
    public bool Enabled { get; set; } = true;
    public int ReminderIntervalMinutes { get; set; } = 15;
    public int ScanIntervalSeconds { get; set; } = 60;
}

/// <summary>
/// Balaie tous les sites et déclenche l'escalade des dépassements. La logique
/// métier (niveaux, anti-spam, reset) vit dans Visit.EvaluateOverstayAlertLevel ;
/// ce scanner l'orchestre par site (tenant résolu) et diffuse les alertes.
/// </summary>
public sealed class OverstayScanner : IOverstayScanner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISiteCatalog _sites;
    private readonly IDateTimeProvider _clock;
    private readonly OverstayOptions _options;
    private readonly ILogger<OverstayScanner> _logger;

    public OverstayScanner(
        IServiceScopeFactory scopeFactory,
        ISiteCatalog sites,
        IDateTimeProvider clock,
        IOptions<OverstayOptions> options,
        ILogger<OverstayScanner> logger)
    {
        _scopeFactory = scopeFactory;
        _sites = sites;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ScanOnceAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
            return;

        var reminderInterval = TimeSpan.FromMinutes(Math.Max(1, _options.ReminderIntervalMinutes));
        var now = _clock.UtcNow;

        foreach (var siteId in await _sites.GetSiteIdsAsync(ct))
        {
            try
            {
                await ScanSiteAsync(siteId, now, reminderInterval, ct);
            }
            catch (Exception ex)
            {
                // Un site en erreur ne doit pas empêcher la supervision des autres.
                _logger.LogWarning(ex, "Supervision des dépassements : échec pour le site {SiteId}.", siteId);
            }
        }
    }

    private async Task ScanSiteAsync(string siteId, DateTimeOffset now, TimeSpan reminderInterval, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<CurrentTenant>().Resolve(siteId);
        var db = sp.GetRequiredService<NovAccesDbContext>();
        var broadcaster = sp.GetRequiredService<IScanEventBroadcaster>();
        var hosts = sp.GetRequiredService<IHostDirectory>();
        var notifications = sp.GetRequiredService<INotificationService>();

        var onSite = await db.Visits.Where(v => v.IsOnSite).ToListAsync(ct);

        var changed = false;
        foreach (var visit in onSite)
        {
            var level = visit.EvaluateOverstayAlertLevel(now, reminderInterval);
            if (level <= 0)
                continue;

            changed = true;
            var overstayMinutes = visit.ComputeOverstayMinutes(now);
            var isSecurityEvent = level >= 3;

            await broadcaster.BroadcastOverstayAsync(
                new OverstayBroadcastEvent(visit.Id, visit.VisitorName, overstayMinutes, level, isSecurityEvent, now), ct);

            // §7 : l'alerte part vers la sûreté (diffusion ci-dessus) ET vers
            // l'hôte, à chaque niveau. Best-effort : une panne d'envoi ne doit
            // ni interrompre le balayage des autres visiteurs, ni empêcher
            // l'enregistrement du niveau atteint.
            try
            {
                var host = await hosts.FindAsync(visit.HostUserId, ct);
                if (host is not null)
                {
                    await notifications.NotifyHostAsync(new HostEventNotification(
                        HostEventKind.Overstay, visit.Id, visit.VisitorName, host, now,
                        OverstayMinutes: overstayMinutes, OverstayLevel: level), ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Dépassement : échec de la notification de l'hôte pour la visite {VisitId}.", visit.Id);
            }

            // Minimisation des données : identifiant opaque de la visite, pas le
            // nom (PII) — le nom reste dans le journal et le dashboard (accès contrôlé).
            _logger.Log(isSecurityEvent ? LogLevel.Warning : LogLevel.Information,
                "Dépassement site {SiteId} : visite {VisitId} +{Min} min (niveau {Level}{Secu}).",
                siteId, visit.Id, overstayMinutes, level, isSecurityEvent ? ", ÉVÉNEMENT SÉCURITÉ" : "");
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }
}
