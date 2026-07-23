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
    private string? _siteId;
    private string? _schemaName;

    public string SiteId => _siteId ?? throw new InvalidOperationException(
        "Tenant non résolu : le middleware de résolution n'a pas été exécuté.");

    public string SchemaName => _schemaName ?? throw new InvalidOperationException(
        "Tenant non résolu : le middleware de résolution n'a pas été exécuté.");

    public bool IsResolved => _siteId is not null;

    public void Resolve(string siteId)
    {
        // Nom de schéma dérivé et assaini : évite toute injection SQL au moment
        // du "SET search_path" (voir NovAccesDbContext). Seuls [a-z0-9_] admis.
        if (string.IsNullOrWhiteSpace(siteId) || !siteId.All(c => char.IsLetterOrDigit(c) || c == '_'))
            throw new ArgumentException("Identifiant de site invalide.", nameof(siteId));

        _siteId = siteId.ToLowerInvariant();
        _schemaName = $"site_{_siteId}";
    }
}
