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

    public void Resolve(string siteId)
    {
        if (!IsValidSiteId(siteId))
            throw new ArgumentException("Identifiant de site invalide.", nameof(siteId));

        _siteId = siteId.ToLowerInvariant();
        _schemaName = $"site_{_siteId}";
    }

    /// <summary>
    /// Whitelist ASCII stricte [a-zA-Z0-9_], appliquée AVANT toute conversion
    /// de casse : ToLowerInvariant() sur certains caractères Unicode (ex. le
    /// 'İ' turc, U+0130) peut produire des caractères combinants absents de
    /// la chaîne d'origine, donc jamais validés si on vérifiait après coup.
    /// char.IsLetterOrDigit() était utilisé précédemment mais accepte
    /// n'importe quelle lettre Unicode, pas seulement [a-z0-9] — contraire au
    /// commentaire d'origine. Longueur bornée : voir MaxSiteIdLength.
    /// Partagé avec ScanEventsHub pour éviter une double implémentation qui
    /// dérive silencieusement de cette règle.
    /// </summary>
    public static bool IsValidSiteId(string? siteId) =>
        !string.IsNullOrWhiteSpace(siteId)
        && siteId.Length <= MaxSiteIdLength
        && siteId.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_');
}
