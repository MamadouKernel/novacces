using NovAcces.Infrastructure.Persistence.Tenancy;

namespace NovAcces.Api.Middleware;

/// <summary>
/// Résout le tenant AVANT tout accès aux données, pour CHAQUE requête :
///  - Portail web (Blazor) : sous-domaine (sicopa.novacces.ci → "sicopa")
///  - Application agent (MAUI) : en-tête X-Site-Id, combiné à la clé API du
///    terminal enrôlé (vérifiée par ApiKeyAuthenticationMiddleware, à ajouter
///    en jalon 2) — jamais fait confiance à l'en-tête seul en production.
///
/// Toute requête dont le tenant ne peut pas être résolu est rejetée en 400
/// AVANT d'atteindre un contrôleur : c'est la garantie qu'aucune requête ne
/// peut accidentellement s'exécuter sans cloisonnement (REQ-F-10).
/// </summary>
public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, CurrentTenant currentTenant)
    {
        // Endpoints exemptés : health check, et le Hub SignalR (/hubs/*) qui
        // valide lui-même son site via la query string à la connexion — une
        // connexion WebSocket persistante ne repasse pas par ce middleware à
        // chaque message, contrairement à une requête HTTP classique.
        if (context.Request.Path.StartsWithSegments("/health")
            || context.Request.Path.StartsWithSegments("/hubs"))
        {
            await _next(context);
            return;
        }

        var siteId = ExtractSiteId(context);

        if (string.IsNullOrWhiteSpace(siteId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "Site non identifié (sous-domaine ou en-tête X-Site-Id requis)." });
            return;
        }

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

        await _next(context);
    }

    private static string? ExtractSiteId(HttpContext context)
    {
        // 1. En-tête explicite (app agent, intégrations)
        if (context.Request.Headers.TryGetValue("X-Site-Id", out var headerValue))
            return headerValue.ToString();

        // 2. Sous-domaine (portail web) : sicopa.novacces.ci -> "sicopa"
        var host = context.Request.Host.Host;
        var parts = host.Split('.');
        if (parts.Length >= 3) // sous-domaine.domaine.tld
            return parts[0];

        return null;
    }
}

public static class TenantResolutionMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app)
        => app.UseMiddleware<TenantResolutionMiddleware>();
}
