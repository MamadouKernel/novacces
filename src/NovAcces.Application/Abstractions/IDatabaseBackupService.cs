namespace NovAcces.Application.Abstractions;

/// <summary>
/// Sauvegarde/administration de la base PostgreSQL COMPLÈTE (schéma partagé
/// "identity" + tous les schémas de site) — distinct de
/// <c>GET /api/admin/sites/{id}/export</c>, qui exporte les données d'UN SEUL
/// site en CSV ouvert pour une restitution de fin de contrat. Ici, il s'agit
/// d'une sauvegarde binaire de reprise après sinistre, réservée au SuperAdmin
/// (moindre privilège §8.5 : une sauvegarde complète expose les données de
/// TOUS les clients de Sigasécurité en un seul fichier).
///
/// La restauration existe (<see cref="RestoreBackupAsync"/>) mais n'est
/// délibérément PAS exposée en HTTP : elle écrase la base en cours pendant
/// que l'API sert du trafic live, ce qu'aucune confirmation de bouton web
/// ne rend sûr. Elle n'est accessible qu'en CLI (Program.cs,
/// "restore-database"), le même registre que "provision-site"/"migrate" —
/// un geste d'exploitation délibéré, pas un clic accidentel.
/// </summary>
public interface IDatabaseBackupService
{
    /// <summary>
    /// Déclenche une sauvegarde immédiate (pg_dump, format personnalisé
    /// compressé). Une seule sauvegarde à la fois : un appel concurrent
    /// pendant qu'une sauvegarde est en cours est refusé (voir
    /// <see cref="DatabaseBackupInProgressException"/>) plutôt que de lancer
    /// deux pg_dump en parallèle contre la même base.
    /// </summary>
    Task<DatabaseBackupInfo> CreateBackupAsync(CancellationToken ct);

    /// <summary>Liste les sauvegardes présentes sur disque, les plus récentes d'abord.</summary>
    Task<IReadOnlyList<DatabaseBackupInfo>> ListBackupsAsync(CancellationToken ct);

    /// <summary>
    /// Ouvre un fichier de sauvegarde pour téléchargement. Retourne null si
    /// le nom ne correspond à aucune sauvegarde existante — l'appelant ne
    /// doit JAMAIS construire un chemin disque à partir d'une entrée non
    /// validée par cette méthode (protection contre la traversée de chemin).
    /// </summary>
    Task<Stream?> OpenBackupForDownloadAsync(string fileName, CancellationToken ct);

    /// <summary>
    /// Restaure la base COMPLÈTE depuis une sauvegarde (pg_restore --clean).
    /// ÉCRASE toutes les données courantes de tous les sites. CLI uniquement
    /// — voir la remarque de classe. Lève si le fichier est introuvable/invalide.
    /// </summary>
    Task RestoreBackupAsync(string fileName, CancellationToken ct);
}

/// <summary>Nom de fichier + taille + date d'une sauvegarde présente sur disque.</summary>
public sealed record DatabaseBackupInfo(string FileName, long SizeBytes, DateTimeOffset CreatedAt);

/// <summary>Levée quand une sauvegarde est déjà en cours d'exécution.</summary>
public sealed class DatabaseBackupInProgressException : Exception
{
    public DatabaseBackupInProgressException()
        : base("Une sauvegarde est déjà en cours ; réessayez dans quelques instants.") { }
}

/// <summary>Levée quand pg_dump échoue (code de sortie non nul).</summary>
public sealed class DatabaseBackupFailedException : Exception
{
    public DatabaseBackupFailedException(string message) : base(message) { }
}
