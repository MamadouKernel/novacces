using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Enums;
using NovAcces.Infrastructure.Identity;
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
    /// <summary>
    /// Sentinel d'anonymisation du nom de visiteur dans scan_logs. DOIT rester
    /// identique à la valeur codée dans le trigger append-only
    /// (TenantProvisioningService.AppendOnlyJournalDdl) : c'est la seule valeur
    /// que le trigger accepte comme nouvelle valeur du nom.
    /// </summary>
    public const string AnonymizationSentinel = "[anonymisé]";

    public bool Enabled { get; set; } = true;

    /// <summary>Durée de conservation des demandes de visite (PII), en jours. &lt;= 0 désactive la purge.</summary>
    public int VisitRetentionDays { get; set; } = 365;

    /// <summary>
    /// Durée de conservation du NOM du visiteur dans le journal des scans, en
    /// jours (fenêtre plus longue : le journal reste une preuve de sécurité, mais
    /// le nom est anonymisé au-delà). &lt;= 0 désactive l'anonymisation.
    /// </summary>
    public int JournalRetentionDays { get; set; } = 1095;

    /// <summary>Intervalle entre deux passes de purge automatique, en heures.</summary>
    public int RunIntervalHours { get; set; } = 24;

    /// <summary>
    /// Durée de conservation du JOURNAL GLOBAL des requêtes API
    /// (ApplicationAudit, schéma partagé), en jours. Ce journal prend une ligne
    /// par requête — y compris les sondes de supervision et les rejets
    /// anonymes : sans purge il croît indéfiniment. Il est technique et
    /// transversal, à distinguer des journaux MÉTIER par site (scan_logs,
    /// admin_audit) qui, eux, ne sont jamais supprimés. &lt;= 0 désactive.
    /// </summary>
    public int ApplicationAuditRetentionDays { get; set; } = 180;

    /// <summary>
    /// Délai de conservation des sessions de rafraîchissement DÉJÀ expirées ou
    /// révoquées, en jours. Elles n'ont plus aucune valeur d'authentification ;
    /// on les garde un court moment pour l'analyse post-incident, puis on
    /// supprime. &lt;= 0 désactive.
    /// </summary>
    public int RefreshSessionRetentionDays { get; set; } = 30;
}

/// <summary>
/// Purge les demandes de visite (PII) au-delà de la durée de conservation, par
/// site. Orchestré comme <c>OverstayScanner</c> : un scope + tenant résolu par
/// site, sur des DbContext scoped.
///
/// DÉCISION DE SÛRETÉ : on ne SUPPRIME que la table <c>visits</c> (données
/// opérationnelles). Les journaux ne sont jamais supprimés (leur inaltérabilité
/// est le socle de la non-répudiation). Pour concilier §7.5 (inaltérable) et
/// §7.3 (rétention limitée), le nom du visiteur dans <c>scan_logs</c> est
/// ANONYMISÉ au-delà de JournalRetentionDays : le trigger append-only autorise
/// exclusivement cette transition (nom → sentinel), rien d'autre. admin_audit
/// ne porte aucun nom de visiteur (minimisation) et reste totalement verrouillé.
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

    public async Task<RetentionPurgeResult> PurgeOnceAsync(CancellationToken ct)
    {
        var results = new List<SitePurgeResult>();

        if (!_options.Enabled)
        {
            _logger.LogInformation("Rétention désactivée (Retention:Enabled=false).");
            return new RetentionPurgeResult(results, 0, 0);
        }

        var now = _clock.UtcNow;

        if (_options.VisitRetentionDays > 0 || _options.JournalRetentionDays > 0)
        {
            foreach (var siteId in await _sites.GetSiteIdsAsync(ct))
            {
                try
                {
                    results.Add(await PurgeSiteAsync(siteId, now, ct));
                }
                catch (Exception ex)
                {
                    // Un site en erreur ne doit pas empêcher le traitement des autres.
                    _logger.LogWarning(ex, "Rétention : échec pour le site {SiteId}.", siteId);
                }
            }
        }

        // Tables du schéma PARTAGÉ : hors tenant, traitées une seule fois par
        // passe. Isolées dans leur propre try : un échec ici ne doit pas
        // invalider le travail déjà fait sur les sites.
        var auditPurged = 0;
        var sessionsPurged = 0;
        try
        {
            (auditPurged, sessionsPurged) = await PurgeSharedTablesAsync(now, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rétention : échec sur les tables transverses (journal global, sessions).");
        }

        return new RetentionPurgeResult(results, auditPurged, sessionsPurged);
    }

    /// <summary>
    /// Purge du schéma partagé « identity ». Contrairement aux journaux métier
    /// par site, ces deux tables sont TECHNIQUES : le journal global tracerait
    /// sinon indéfiniment chaque sonde de supervision, et les sessions de
    /// rafraîchissement révoquées n'ont plus aucune valeur d'authentification.
    /// </summary>
    private async Task<(int AuditPurged, int SessionsPurged)> PurgeSharedTablesAsync(
        DateTimeOffset now, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var identityDb = scope.ServiceProvider.GetRequiredService<NovAccesIdentityDbContext>();

        var auditPurged = 0;
        if (_options.ApplicationAuditRetentionDays > 0)
        {
            var cutoff = now.AddDays(-_options.ApplicationAuditRetentionDays);
            auditPurged = await identityDb.ApplicationAudit
                .Where(e => e.Timestamp < cutoff)
                .ExecuteDeleteAsync(ct);

            if (auditPurged > 0)
                _logger.LogInformation(
                    "Rétention : {Count} ligne(s) du journal global supprimée(s) (antérieures au {Cutoff:yyyy-MM-dd}).",
                    auditPurged, cutoff);
        }

        var sessionsPurged = 0;
        if (_options.RefreshSessionRetentionDays > 0)
        {
            var cutoff = now.AddDays(-_options.RefreshSessionRetentionDays);

            // Uniquement les sessions DÉJÀ mortes (expirées ou révoquées) et
            // dont la mort remonte à plus que la fenêtre : jamais une session
            // encore utilisable, quelle que soit son ancienneté.
            sessionsPurged = await identityDb.RefreshSessions
                .Where(s => (s.ExpiresAt < cutoff) || (s.RevokedAt != null && s.RevokedAt < cutoff))
                .ExecuteDeleteAsync(ct);

            if (sessionsPurged > 0)
                _logger.LogInformation(
                    "Rétention : {Count} session(s) de rafraîchissement expirée(s)/révoquée(s) supprimée(s).",
                    sessionsPurged);
        }

        return (auditPurged, sessionsPurged);
    }

    private async Task<SitePurgeResult> PurgeSiteAsync(string siteId, DateTimeOffset now, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<CurrentTenant>().Resolve(siteId);
        var db = sp.GetRequiredService<NovAccesDbContext>();
        var audit = sp.GetRequiredService<IAdminAuditLog>();

        var purged = 0;
        var anonymized = 0;

        // 1) Suppression des demandes de visite (PII) au-delà de la conservation.
        //    Jamais un visiteur encore sur site (IsOnSite) — la sécurité prime.
        if (_options.VisitRetentionDays > 0)
        {
            var cutoff = now.AddDays(-_options.VisitRetentionDays);
            purged = await db.Visits
                .Where(v => v.CreatedAt < cutoff && !v.IsOnSite)
                .ExecuteDeleteAsync(ct);

            if (purged > 0)
            {
                _logger.LogInformation(
                    "Rétention site {SiteId} : {Count} demande(s) supprimée(s) (antérieures au {Cutoff:yyyy-MM-dd}).",
                    siteId, purged, cutoff);
                await audit.RecordAsync(
                    AdminAuditAction.DataPurged, "système (rétention)", null,
                    $"{purged} demande(s) purgée(s) (conservation {_options.VisitRetentionDays} j, seuil {cutoff:yyyy-MM-dd}).",
                    ct);
            }
        }

        // 2) Anonymisation du nom du visiteur dans le journal des scans au-delà de
        //    la conservation du journal. Le trigger append-only n'autorise QUE le
        //    passage du nom au sentinel : cette requête est la seule mutation
        //    possible sur scan_logs. Le search_path (tenant) est posé par
        //    l'intercepteur, donc « scan_logs » vise bien le schéma du site.
        if (_options.JournalRetentionDays > 0)
        {
            var journalCutoff = now.AddDays(-_options.JournalRetentionDays);
            anonymized = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE scan_logs SET "VisitorName" = {RetentionOptions.AnonymizationSentinel}
                 WHERE "Timestamp" < {journalCutoff} AND "VisitorName" <> {RetentionOptions.AnonymizationSentinel}
                 """, ct);

            if (anonymized > 0)
            {
                _logger.LogInformation(
                    "Rétention site {SiteId} : {Count} nom(s) anonymisé(s) dans le journal des scans (antérieurs au {Cutoff:yyyy-MM-dd}).",
                    siteId, anonymized, journalCutoff);
                await audit.RecordAsync(
                    AdminAuditAction.JournalAnonymized, "système (rétention)", null,
                    $"{anonymized} nom(s) anonymisé(s) dans scan_logs (conservation {_options.JournalRetentionDays} j, seuil {journalCutoff:yyyy-MM-dd}).",
                    ct);
            }
        }

        return new SitePurgeResult(siteId, purged, anonymized);
    }
}
