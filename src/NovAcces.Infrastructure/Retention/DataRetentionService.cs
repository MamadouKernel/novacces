using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Enums;
using NovAcces.Infrastructure.Persistence;
using NovAcces.Infrastructure.Persistence.Tenancy;

namespace NovAcces.Infrastructure.Retention;

/// <summary>
/// Paramètres de rétention des données (section 7.3). Durée exprimée en jours,
/// paramétrable par déploiement. Une valeur de 0 ou négative désactive la purge
/// (garde-fou : on ne purge jamais « tout » par une mauvaise configuration).
/// </summary>
public sealed class RetentionOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Durée de conservation des demandes de visite, en jours.</summary>
    public int VisitRetentionDays { get; set; } = 365;

    /// <summary>Intervalle entre deux passes de purge automatique, en heures.</summary>
    public int RunIntervalHours { get; set; } = 24;
}

/// <summary>
/// Purge les demandes de visite (PII) au-delà de la durée de conservation, par
/// site. Orchestré comme <c>OverstayScanner</c> : un scope + tenant résolu par
/// site, sur des DbContext scoped.
///
/// DÉCISION DE SÛRETÉ (à valider — voir CLAUDE.md) : cette purge ne touche QUE
/// la table <c>visits</c>. Les journaux inaltérables (scan_logs, admin_audit)
/// sont délibérément épargnés — les purger depuis l'application contredirait
/// leur inaltérabilité (triggers append-only) et ouvrirait une voie de
/// suppression de preuves. La conservation/anonymisation de ces journaux relève
/// d'une procédure d'exploitation privilégiée et documentée, hors application.
/// </summary>
public sealed class DataRetentionService : IDataRetentionService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISiteCatalog _sites;
    private readonly IDateTimeProvider _clock;
    private readonly RetentionOptions _options;
    private readonly ILogger<DataRetentionService> _logger;

    public DataRetentionService(
        IServiceScopeFactory scopeFactory,
        ISiteCatalog sites,
        IDateTimeProvider clock,
        IOptions<RetentionOptions> options,
        ILogger<DataRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _sites = sites;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SitePurgeResult>> PurgeOnceAsync(CancellationToken ct)
    {
        var results = new List<SitePurgeResult>();

        // Garde-fou : une durée nulle/négative ou la désactivation explicite
        // n'entraîne aucune suppression.
        if (!_options.Enabled || _options.VisitRetentionDays <= 0)
        {
            _logger.LogInformation(
                "Purge de rétention désactivée (Enabled={Enabled}, VisitRetentionDays={Days}).",
                _options.Enabled, _options.VisitRetentionDays);
            return results;
        }

        var cutoff = _clock.UtcNow.AddDays(-_options.VisitRetentionDays);

        foreach (var siteId in await _sites.GetSiteIdsAsync(ct))
        {
            try
            {
                var purged = await PurgeSiteAsync(siteId, cutoff, ct);
                results.Add(new SitePurgeResult(siteId, purged));
            }
            catch (Exception ex)
            {
                // Un site en erreur ne doit pas empêcher la purge des autres.
                _logger.LogWarning(ex, "Purge de rétention : échec pour le site {SiteId}.", siteId);
            }
        }

        return results;
    }

    private async Task<int> PurgeSiteAsync(string siteId, DateTimeOffset cutoff, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<CurrentTenant>().Resolve(siteId);
        var db = sp.GetRequiredService<NovAccesDbContext>();

        // On ne purge que des demandes TERMINÉES : jamais un visiteur encore sur
        // site (IsOnSite), quelle que soit son ancienneté — la sécurité prime.
        var purged = await db.Visits
            .Where(v => v.CreatedAt < cutoff && !v.IsOnSite)
            .ExecuteDeleteAsync(ct);

        if (purged > 0)
        {
            _logger.LogInformation(
                "Purge de rétention site {SiteId} : {Count} demande(s) supprimée(s) (antérieures au {Cutoff:yyyy-MM-dd}).",
                siteId, purged, cutoff);

            // Trace §8.5 : la purge est une action privilégiée, inscrite au
            // journal d'audit inaltérable du site (acteur = traitement système).
            var audit = sp.GetRequiredService<IAdminAuditLog>();
            await audit.RecordAsync(
                AdminAuditAction.DataPurged, "système (rétention)", null,
                $"{purged} demande(s) purgée(s) (conservation {_options.VisitRetentionDays} j, seuil {cutoff:yyyy-MM-dd}).",
                ct);
        }

        return purged;
    }
}
