namespace NovAcces.Application.Abstractions;

/// <summary>
/// Le site client (tenant) de la requête en cours. Résolu par le middleware
/// (sous-domaine pour le web, en-tête X-Site-Id + clé API pour l'app agent).
/// Chaque tenant correspond à un schéma PostgreSQL isolé (REQ-F-10 du CDC) —
/// voir Infrastructure/Persistence/Tenancy/TenantSchemaResolver.
/// </summary>
public interface ICurrentTenant
{
    /// <summary>Identifiant technique du site (ex. "sicopa").</summary>
    string SiteId { get; }

    /// <summary>Nom du schéma PostgreSQL associé (ex. "site_sicopa").</summary>
    string SchemaName { get; }

    bool IsResolved { get; }
}
