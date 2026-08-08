using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Implémentation scoped (une instance par requête HTTP) de ICurrentTenant.
/// Renseignée exclusivement par TenantResolutionMiddleware (Api) — jamais
/// directement par le code métier, pour garantir qu'aucune requête ne
/// s'exécute sans tenant résolu.
/// </summary>
public sealed class CurrentTenant : ICurrentTenant
{
    // "site_" (5 caractères) + MaxSiteIdLength doit rester <= 63 octets
    // (NAMEDATALEN de PostgreSQL) : au-delà, l'identifiant de schéma serait
    // tronqué silencieusement par PostgreSQL, ce qui pourrait faire
    // collisionner deux tenants distincts sur le même schéma physique.
    private const int MaxSiteIdLength = 40;

    private string? _siteId;
    private string? _schemaName;

    public string SiteId => _siteId ?? throw new InvalidOperationException(
        "Tenant non résolu : le middleware de résolution n'a pas été exécuté.");

    public string SchemaName => _schemaName ?? throw new InvalidOperationException(
        "Tenant non résolu : le middleware de résolution n'a pas été exécuté.");

    public bool IsResolved => _siteId is not null;

    public static string NormalizeSiteId(string siteId) =>
        (siteId ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    public void Resolve(string siteId)
    {
        if (!IsValidSiteId(siteId))
            throw new ArgumentException("Identifiant de site invalide.", nameof(siteId));

        _siteId = NormalizeSiteId(siteId);
        _schemaName = $"site_{_siteId}";
    }

    /// <summary>
    /// Whitelist ASCII stricte [a-zA-Z0-9_-] (espaces et tirets automatiquement
    /// normalisés en soulignés '_'), appliquée AVANT toute conversion de casse.
    /// Longueur bornée : voir MaxSiteIdLength.
    /// Partagé avec ScanEventsHub et les filtres d'en-têtes.
    /// </summary>
    public static bool IsValidSiteId(string? siteId)
    {
        if (string.IsNullOrWhiteSpace(siteId) || siteId.Length > MaxSiteIdLength)
            return false;

        var normalized = NormalizeSiteId(siteId);
        return normalized.Length > 0 && normalized.Length <= MaxSiteIdLength
            && normalized.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_');
    }
}
