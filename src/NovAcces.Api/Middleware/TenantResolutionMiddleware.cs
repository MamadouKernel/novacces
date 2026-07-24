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
///  - Utilisateur rattaché à un site (Hôte / Sûreté / Agent) : tenant = son claim
///    SiteId. Un en-tête X-Site-Id divergent est rejeté (tentative d'accès à un
///    autre site que le sien = 403).
///  - Admin global (aucun claim SiteId) : peut cibler un site via X-Site-Id.
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
            // Pas de site dans l'identité (Admin global, ou requête non encore
            // authentifiée arrivant sur un endpoint qui sera protégé plus loin).
            siteId = headerSite;
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
        || path.StartsWithSegments("/hubs")
        || path.StartsWithSegments("/swagger")
        || path.StartsWithSegments("/api/auth");
}

public static class TenantResolutionMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app)
        => app.UseMiddleware<TenantResolutionMiddleware>();
}
