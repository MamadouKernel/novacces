namespace NovAcces.Application.Abstractions;

/// <summary>
/// Provisionnement d'un nouveau site (tenant) : création du schéma PostgreSQL
/// dédié, application du modèle de données, et durcissement du journal en
/// append-only. Opération d'administration, idempotente, invoquée hors du
/// cycle de requête (ligne de commande d'exploitation) — voir
/// Infrastructure/Persistence/Tenancy/TenantProvisioningService.
///
/// Centralise ce qui était jusqu'ici une suite d'étapes SQL manuelles
/// (cf. README §6), pour qu'ajouter un client de Sigasécurité soit une
/// opération unique, reproductible et testée.
/// </summary>
public interface ITenantProvisioningService
{
    /// <param name="siteId">Identifiant technique du site (ex. "sicopa").</param>
    Task ProvisionAsync(string siteId, CancellationToken ct = default);
}
