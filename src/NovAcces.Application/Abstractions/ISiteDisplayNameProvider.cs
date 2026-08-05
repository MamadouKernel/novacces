namespace NovAcces.Application.Abstractions;

/// <summary>
/// Nom lisible d'un site (ex. "SICOPA — Terminal portuaire"), à afficher aux
/// visiteurs/hôtes — distinct de <see cref="ICurrentTenant.SiteId"/>, qui est
/// l'identifiant technique du schéma PostgreSQL (ex. "sicopa"), jamais destiné
/// à être lu par un humain hors du personnel technique.
/// </summary>
public interface ISiteDisplayNameProvider
{
    /// <summary>Replié sur l'identifiant technique si aucun libellé n'est configuré pour ce site.</summary>
    string GetLabel(string siteId);
}
