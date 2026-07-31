using Microsoft.AspNetCore.Identity;

namespace NovAcces.Infrastructure.Identity;

/// <summary>
/// Compte utilisateur du portail web (Hôte / Sûreté / Admin). Les agents de
/// contrôle n'ont PAS de compte ici : le client mobile React Native s'authentifie par jeton agent (les routes API Key restent historiques)
/// terminal enrôlé (voir Auth/ApiKeyAuthenticationHandler).
///
/// Modèle multi-tenant : tables Identity dans un schéma PARTAGÉ (« identity »),
/// chaque utilisateur portant son <see cref="SiteId"/> de rattachement. Ce choix
/// évite le problème œuf-poule d'une résolution de tenant AVANT le login, et
/// garde l'administration cross-sites simple. Le cloisonnement des données
/// métier reste, lui, strict par schéma (voir TenantSchemaConnectionInterceptor).
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>
    /// Site de rattachement (ex. "sicopa"). NULL pour un Admin Sigasécurité
    /// global, qui opère sur tous les sites. Reporté dans un claim à la
    /// connexion et utilisé comme tenant des requêtes de cet utilisateur.
    /// </summary>
    public string? SiteId { get; set; }

    /// <summary>Nom affiché (journalisation, dashboard).</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Désactivation logique : les données sont conservées pour audit et conformité.</summary>
    public bool IsDeactivated { get; private set; }
    public DateTimeOffset? DeactivatedAt { get; private set; }

    public void Deactivate(DateTimeOffset now)
    {
        IsDeactivated = true;
        DeactivatedAt = now;
        LockoutEnd = now.AddYears(100);
        SecurityStamp = Guid.NewGuid().ToString("N");
    }
}
