using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure.Persistence;

namespace NovAcces.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Agrège l'activité de chaque site (présents + scans du jour) en résolvant le
/// tenant site par site, sur le même principe que la supervision des dépassements.
/// </summary>
public sealed class SiteOverviewService : ISiteOverviewService
{
    /// <summary>
    /// Au-delà de ce délai sans le moindre scan, un poste est considéré
    /// silencieux. Volontairement large : un poste d'entrée peut légitimement
    /// ne rien scanner pendant une heure creuse, ce n'est pas une panne.
    /// </summary>
    private static readonly TimeSpan TerminalActivityWindow = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISiteCatalog _sites;
    private readonly ITerminalDirectory _terminals;
    private readonly IDateTimeProvider _clock;

    public SiteOverviewService(
        IServiceScopeFactory scopeFactory,
        ISiteCatalog sites,
        ITerminalDirectory terminals,
        IDateTimeProvider clock)
    {
        _scopeFactory = scopeFactory;
        _sites = sites;
        _terminals = terminals;
        _clock = clock;
    }

    public async Task<IReadOnlyList<SiteOverview>> GetAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var activitySince = now - TerminalActivityWindow;
        var result = new List<SiteOverview>();

        // Les terminaux vivent dans le schéma PARTAGÉ (un terminal peut servir
        // plusieurs sites) : une seule lecture, réutilisée pour tous les sites.
        var allTerminals = await _terminals.ListAsync(ct);

        foreach (var siteId in await _sites.GetSiteIdsAsync(ct))
        {
            var enrolled = allTerminals.Count(t =>
                t.IsActive && t.IsEnrolled && t.SiteIds.Contains(siteId, StringComparer.OrdinalIgnoreCase));

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;
                sp.GetRequiredService<CurrentTenant>().Resolve(siteId);
                var db = sp.GetRequiredService<NovAccesDbContext>();

                var onSite = await db.Visits.CountAsync(v => v.IsOnSite, ct);
                var scansToday = await db.ScanLogs.CountAsync(e => e.Timestamp >= dayStart, ct);
                var degradedToday = await db.ScanLogs
                    .CountAsync(e => e.Timestamp >= dayStart && e.RecordedInDegradedMode, ct);

                // « Actif » = a produit au moins un scan dans la fenêtre. On
                // compte les AgentId distincts : c'est la trace qu'un poste a
                // effectivement fonctionné, la seule dont on dispose côté serveur.
                var active = await db.ScanLogs
                    .Where(e => e.Timestamp >= activitySince)
                    .Select(e => e.AgentId)
                    .Distinct()
                    .CountAsync(ct);

                result.Add(new SiteOverview(
                    siteId, onSite, scansToday, enrolled, Math.Min(active, enrolled), degradedToday));
            }
            catch
            {
                // Un site indisponible ne doit pas casser la vue consolidée :
                // on le montre sans activité plutôt que de masquer la ligne.
                result.Add(new SiteOverview(siteId, 0, 0, enrolled, 0, 0));
            }
        }

        return result.OrderBy(s => s.SiteId).ToList();
    }
}
