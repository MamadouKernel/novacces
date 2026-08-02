namespace NovAcces.Application.Abstractions;

/// <summary>
/// Tendance d'activité sur plusieurs jours, agrégée à travers tous les sites
/// (dashboard Admin — analyse de données au-delà de l'instantané du jour).
/// </summary>
public interface ISiteTrendsService
{
    /// <summary>1 à 90 jours (borné côté implémentation).</summary>
    Task<SiteTrendsResult> GetAsync(int days, CancellationToken ct);
}

/// <summary>Point d'une courbe journalière, tous sites confondus.</summary>
public sealed record DailyTrendPoint(
    DateOnly Date, int ScansTotal, int EntriesGranted, int Exits, int Denied, int SecurityEvents);

/// <summary>Total d'un site sur la période demandée.</summary>
public sealed record SiteActivityTotal(string SiteId, int ScansTotal);

public sealed record SiteTrendsResult(
    IReadOnlyList<DailyTrendPoint> Daily,
    IReadOnlyList<SiteActivityTotal> BySite);
