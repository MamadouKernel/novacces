namespace NovAcces.Application.Abstractions;

/// <summary>
/// Purge des données personnelles au-delà de la durée de conservation
/// paramétrée (section 7.3 du CDC — conformité protection des données
/// personnelles ivoirienne / ARTCI). Balaie tous les sites, comme la
/// supervision des dépassements.
///
/// Périmètre volontairement limité aux données OPÉRATIONNELLES porteuses de
/// PII (les demandes de visite : nom, téléphone, email). Les journaux
/// inaltérables (scan_logs, admin_audit) ne sont PAS purgés par l'application :
/// leur conservation relève d'une décision d'exploitation distincte et d'une
/// procédure privilégiée en base — voir DataRetentionService pour le détail.
/// </summary>
public interface IDataRetentionService
{
    /// <summary>Exécute une passe de purge sur tous les sites. Idempotente.</summary>
    Task<IReadOnlyList<SitePurgeResult>> PurgeOnceAsync(CancellationToken ct);
}

/// <summary>Décompte des demandes purgées pour un site lors d'une passe.</summary>
public sealed record SitePurgeResult(string SiteId, int VisitsPurged);
