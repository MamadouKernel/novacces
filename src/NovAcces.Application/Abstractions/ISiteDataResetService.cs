namespace NovAcces.Application.Abstractions;

/// <summary>
/// Réinitialisation COMPLÈTE d'un site pour repartir de zéro en phase de
/// test/pilote — pas un TRUNCATE brut (toujours bloqué par les triggers
/// append-only, y compris pour ce service) mais un DROP + re-provisionnement
/// du schéma du site (visites, scans, exclusions, audit) suivi d'une purge
/// ciblée du schéma partagé "identity" pour ce site (comptes Hôte/Sûreté
/// rattachés, terminaux dédiés à ce seul site, sessions, registre d'agents).
///
/// Volontairement CLI uniquement (Program.cs, "reset-site-data"), jamais un
/// endpoint HTTP — même raisonnement que IDatabaseBackupService.RestoreBackupAsync :
/// irréversible, un site à la fois, ne doit jamais être un clic accidentel.
///
/// N'affecte JAMAIS : les autres sites, les comptes Admin/SuperAdmin
/// (globaux, sans SiteId), un terminal partagé avec un autre site (retiré
/// de la liste des sites qu'il sert, pas supprimé), l'enregistrement du site
/// lui-même dans identity.sites (le site reste provisionné/actif, seules ses
/// données sont vidées).
/// </summary>
public interface ISiteDataResetService
{
    Task ResetSiteAsync(string siteId, CancellationToken ct);
}
