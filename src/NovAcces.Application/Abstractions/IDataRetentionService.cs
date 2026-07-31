namespace NovAcces.Application.Abstractions;

/// <summary>
/// Purge des données personnelles au-delà de la durée de conservation
/// paramétrée (section 7.3 du CDC — conformité protection des données
/// personnelles ivoirienne / ARTCI). Balaie tous les sites, comme la
/// supervision des dépassements.
///
/// Deux traitements complémentaires (RGPD/ARTCI) :
///  - les demandes de visite (PII : nom, téléphone, email) sont SUPPRIMÉES
///    au-delà de VisitRetentionDays — sauf un visiteur encore sur site ;
///  - le NOM du visiteur dans le journal des scans (scan_logs) est ANONYMISÉ
///    (jamais supprimé) au-delà de JournalRetentionDays : le journal reste une
///    preuve de sécurité inaltérable, mais ne conserve plus l'identité.
/// Le journal d'audit admin (admin_audit) ne contient aucun nom de visiteur
/// (minimisation) : il n'a rien à anonymiser et reste totalement verrouillé.
/// </summary>
public interface IDataRetentionService
{
    /// <summary>Exécute une passe de rétention sur tous les sites. Idempotente.</summary>
    Task<IReadOnlyList<SitePurgeResult>> PurgeOnceAsync(CancellationToken ct);
}

/// <summary>Décompte des opérations de rétention pour un site lors d'une passe.</summary>
public sealed record SitePurgeResult(string SiteId, int VisitsPurged, int ScanLogsAnonymized);
