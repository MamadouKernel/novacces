using System.Security.Claims;
using NovAcces.Infrastructure.Persistence.Tenancy;
using NovAcces.Shared.Auth;

namespace NovAcces.Api.Middleware;

/// <summary>
/// Résout le tenant AVANT tout accès aux données métier (REQ-F-10). Placé APRÈS
/// l'authentification : le site provient en priorité du claim SiteId du principal
/// authentifié (JWT d'un utilisateur web, ou clé API d'un terminal agent), et NON
/// d'un en-tête X-Site-Id falsifiable.
///
///  - Utilisateur rattaché à un site (Hôte / Sûreté / Agent, terminal à un seul
///    site) : tenant = son claim SiteId. Un en-tête X-Site-Id divergent est
///    rejeté (tentative d'accès à un autre site que le sien = 403).
///  - Terminal partagé entre plusieurs sites (claims AllowedSite, pas de claim
///    SiteId unique) : l'en-tête X-Site-Id est OBLIGATOIRE et DOIT figurer
///    dans la liste AllowedSite du terminal — un site hors liste est un 403,
///    tout comme une requête sans en-tête. C'est le choix fait par l'agent à
///    la prise de poste, mais jamais un site arbitraire (cloisonnement §7.3).
///  - Admin global (aucun claim SiteId ni AllowedSite) : peut cibler un site
///    via X-Site-Id, sans restriction (confiance déjà accordée par le rôle).
///  - Requête non authentifiée : aucun tenant résolu ; l'autorisation rejettera
///    l'accès aux endpoints protégés (401/403). Les endpoints publics (login,
///    health, hub) sont exemptés ci-dessous.
/// </summary>
public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, CurrentTenant currentTenant)
    {
        if (IsExempt(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var claimSite = context.User?.FindFirstValue(NovAccesClaimTypes.SiteId);
        var headerSite = context.Request.Headers.TryGetValue("X-Site-Id", out var h) ? h.ToString() : null;

        string? siteId;
        if (!string.IsNullOrWhiteSpace(claimSite))
        {
            // Le site de l'utilisateur fait foi. Un en-tête qui tenterait de viser
            // un AUTRE site est une tentative d'évasion de tenant : on refuse.
            if (!string.IsNullOrWhiteSpace(headerSite)
                && !string.Equals(headerSite, claimSite, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "Site demandé incohérent avec le compte." });
                return;
            }
            siteId = claimSite;
        }
        else
        {
            var allowedSites = context.User?.FindAll(NovAccesClaimTypes.AllowedSite)
                .Select(c => c.Value).ToList() ?? new List<string>();

            if (allowedSites.Count > 0)
            {
                // Terminal partagé entre plusieurs sites : l'agent DOIT avoir choisi
                // un site (X-Site-Id), et UNIQUEMENT parmi ceux autorisés pour ce
                // terminal — jamais un site quelconque du parc.
                if (string.IsNullOrWhiteSpace(headerSite)
                    || !allowedSites.Contains(headerSite, StringComparer.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { error = "Site non autorisé pour ce terminal." });
                    return;
                }
                siteId = headerSite;
            }
            else
            {
                // Pas de site dans l'identité (Admin global, ou requête non encore
                // authentifiée arrivant sur un endpoint qui sera protégé plus loin).
                siteId = headerSite;
            }
        }

        if (!string.IsNullOrWhiteSpace(siteId))
        {
            try
            {
                currentTenant.Resolve(siteId);
            }
            catch (ArgumentException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "Identifiant de site invalide." });
                return;
            }
        }

        await _next(context);
    }

    private static bool IsExempt(PathString path) =>
        path.StartsWithSegments("/health")
        || path.StartsWithSegments("/api/health")
        || path.StartsWithSegments("/hubs")
        || path.StartsWithSegments("/swagger")
        || path.StartsWithSegments("/api/auth")
        // Lit uniquement les claims du terminal (sites qu'il est autorisé à
        // servir) pour peupler le sélecteur de site à la prise de poste — ne
        // touche aucune donnée de tenant, donc pas besoin de site résolu.
        || path.StartsWithSegments("/api/agent/sites")
        || path.StartsWithSegments("/api/keys/public");
}

public static class TenantResolutionMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app)
        => app.UseMiddleware<TenantResolutionMiddleware>();
}
