namespace NovAcces.Application.Abstractions;

/// <summary>
/// Copie une sauvegarde (déjà chiffrée sur disque, voir
/// <see cref="IDatabaseBackupService"/>) vers un stockage HORS du VPS de
/// production — condition d'isolement anti-rançongiciel (§7.4) : un volume
/// Docker sur le même serveur que la production ne protège de rien si le
/// serveur lui-même est compromis. Best-effort par construction : un échec
/// d'upload ne doit jamais faire échouer la sauvegarde locale elle-même, qui
/// reste la protection de premier niveau.
/// </summary>
public interface IBackupOffsiteUploader
{
    Task UploadAsync(string localFilePath, string objectName, CancellationToken ct);
}
