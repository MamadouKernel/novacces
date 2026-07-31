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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISiteCatalog _sites;
    private readonly IDateTimeProvider _clock;

    public SiteOverviewService(IServiceScopeFactory scopeFactory, ISiteCatalog sites, IDateTimeProvider clock)
    {
        _scopeFactory = scopeFactory;
        _sites = sites;
        _clock = clock;
    }

    public async Task<IReadOnlyList<SiteOverview>> GetAsync(CancellationToken ct)
    {
        var dayStart = new DateTimeOffset(_clock.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        var result = new List<SiteOverview>();

        foreach (var siteId in await _sites.GetSiteIdsAsync(ct))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;
                sp.GetRequiredService<CurrentTenant>().Resolve(siteId);
                var db = sp.GetRequiredService<NovAccesDbContext>();

                var onSite = await db.Visits.CountAsync(v => v.IsOnSite, ct);
                var scansToday = await db.ScanLogs.CountAsync(e => e.Timestamp >= dayStart, ct);

                result.Add(new SiteOverview(siteId, onSite, scansToday));
            }
            catch
            {
                // Un site indisponible ne doit pas casser la vue consolidée.
                result.Add(new SiteOverview(siteId, 0, 0));
            }
        }

        return result.OrderBy(s => s.SiteId).ToList();
    }
}
