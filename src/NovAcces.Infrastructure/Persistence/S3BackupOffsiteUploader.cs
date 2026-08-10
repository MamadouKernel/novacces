using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Transfer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Persistence;

/// <summary>
/// Options de réplication hors-site (§7.4) — désactivées par défaut : les
/// identifiants d'un compartiment S3 (ou compatible : Contabo Object Storage,
/// Scaleway, MinIO…) sont fournis par Mamadou au déploiement, pas générés
/// ici. Best-effort partout : une panne d'upload ne remet jamais en cause la
/// sauvegarde locale déjà écrite avec succès.
/// </summary>
public sealed class OffsiteBackupOptions
{
    public bool Enabled { get; set; }

    /// <summary>Point de terminaison S3-compatible (ex. https://eu2.contabostorage.com).</summary>
    public string ServiceUrl { get; set; } = "";

    public string BucketName { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";

    /// <summary>Préfixe d'objet optionnel (ex. "novacces/sicopa/").</summary>
    public string Prefix { get; set; } = "";
}

public sealed class S3BackupOffsiteUploader : IBackupOffsiteUploader
{
    private readonly OffsiteBackupOptions _options;
    private readonly ILogger<S3BackupOffsiteUploader> _logger;

    public S3BackupOffsiteUploader(IOptions<OffsiteBackupOptions> options, ILogger<S3BackupOffsiteUploader> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task UploadAsync(string localFilePath, string objectName, CancellationToken ct)
    {
        if (!_options.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(_options.ServiceUrl)
            || string.IsNullOrWhiteSpace(_options.BucketName)
            || string.IsNullOrWhiteSpace(_options.AccessKey)
            || string.IsNullOrWhiteSpace(_options.SecretKey))
        {
            _logger.LogWarning(
                "Réplication hors-site des sauvegardes activée mais mal configurée (endpoint/bucket/identifiants manquants) — ignorée.");
            return;
        }

        var config = new AmazonS3Config
        {
            ServiceURL = _options.ServiceUrl,
            ForcePathStyle = true,
        };

        using var client = new AmazonS3Client(new BasicAWSCredentials(_options.AccessKey, _options.SecretKey), config);
        using var transfer = new TransferUtility(client);

        var key = string.IsNullOrEmpty(_options.Prefix) ? objectName : _options.Prefix.TrimEnd('/') + "/" + objectName;
        await transfer.UploadAsync(localFilePath, _options.BucketName, key, ct);

        _logger.LogInformation("Sauvegarde répliquée hors-site : {Key}.", key);
    }
}
