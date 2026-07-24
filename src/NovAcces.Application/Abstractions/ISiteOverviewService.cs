namespace NovAcces.Application.Abstractions;

/// <summary>
/// Vue consolidée multi-sites (§10) : pour chaque site provisionné, l'activité
/// du jour et les visiteurs présents. Transverse aux tenants (réservé à l'Admin).
/// </summary>
public interface ISiteOverviewService
{
    Task<IReadOnlyList<SiteOverview>> GetAsync(CancellationToken ct);
}

public sealed record SiteOverview(string SiteId, int OnSite, int ScansToday);
