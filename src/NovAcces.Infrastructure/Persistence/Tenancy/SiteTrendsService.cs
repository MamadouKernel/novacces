using Microsoft.Extensions.DependencyInjection;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Agrège l'activité de chaque site jour par jour, en résolvant le tenant
/// site par site — même principe que <see cref="SiteOverviewService"/>, mais
/// sur une fenêtre de plusieurs jours plutôt que l'instantané du jour.
/// </summary>
public sealed class SiteTrendsService : ISiteTrendsService
{
    private const int MinDays = 1;
    private const int MaxDays = 90;
    private const int RepeatedDenialThreshold = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISiteCatalog _sites;
    private readonly IDateTimeProvider _clock;

    public SiteTrendsService(IServiceScopeFactory scopeFactory, ISiteCatalog sites, IDateTimeProvider clock)
    {
        _scopeFactory = scopeFactory;
        _sites = sites;
        _clock = clock;
    }

    public async Task<SiteTrendsResult> GetAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days, MinDays, MaxDays);
        var now = _clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime.Date);
        var sinceUtc = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(-(days - 1));

        var buckets = new Dictionary<DateOnly, (int Scans, int Entries, int Exits, int Denied, int Security)>();
        var siteTotals = new List<SiteActivityTotal>();
        var repeatedDenials = new List<RepeatedDenial>();
        var denialWindowUtc = now.AddHours(-24);

        foreach (var siteId in await _sites.GetSiteIdsAsync(ct))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;
                sp.GetRequiredService<CurrentTenant>().Resolve(siteId);
                var logs = sp.GetRequiredService<IScanLogRepository>();

                var entries = await logs.GetSinceAsync(sinceUtc, ct);
                siteTotals.Add(new SiteActivityTotal(siteId, entries.Count));

                foreach (var e in entries)
                {
                    var date = DateOnly.FromDateTime(e.Timestamp.ToLocalTime().Date);
                    buckets.TryGetValue(date, out var b);
                    b.Scans++;
                    if (e.WasCheckOut) b.Exits++;
                    else if (e.WasGranted) b.Entries++;
                    if (!e.WasGranted) b.Denied++;
                    if (e.IsSecurityEvent) b.Security++;
                    buckets[date] = b;
                }

                repeatedDenials.AddRange(entries
                    .Where(e => !e.WasGranted && !e.WasCheckOut && e.Timestamp >= denialWindowUtc)
                    .GroupBy(e => e.VisitorName, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() >= RepeatedDenialThreshold)
                    .Select(g => new RepeatedDenial(siteId, g.Key, g.Count(), g.Max(e => e.Timestamp))));
            }
            catch
            {
                // Un site indisponible ne doit pas casser la tendance globale :
                // il apparaît simplement sans activité (cohérent avec SiteOverviewService).
                siteTotals.Add(new SiteActivityTotal(siteId, 0));
            }
        }

        var daily = new List<DailyTrendPoint>();
        for (var d = DateOnly.FromDateTime(sinceUtc.LocalDateTime.Date); d <= today; d = d.AddDays(1))
        {
            buckets.TryGetValue(d, out var b);
            daily.Add(new DailyTrendPoint(d, b.Scans, b.Entries, b.Exits, b.Denied, b.Security));
        }

        return new SiteTrendsResult(
            daily,
            siteTotals.OrderByDescending(s => s.ScansTotal).ToList(),
            repeatedDenials.OrderByDescending(d => d.Count).ToList());
    }
}
