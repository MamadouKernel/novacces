using NovAcces.Infrastructure.Persistence.Tenancy;

namespace NovAcces.Api.Endpoints;

/// <summary>
/// Applique la règle d'en-tête du contrat mobile aux appels Bearer Agent.
/// Les anciens terminaux X-Api-Key restent compatibles et continuent d'utiliser
/// leur résolution de site historique.
/// </summary>
public sealed class ContractSiteHeaderFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        var authorization = request.Headers.Authorization.ToString();
        var isBearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
        if (!isBearer)
            return await next(context);

        var siteId = request.Headers["X-Site-Id"].ToString();
        if (!CurrentTenant.IsValidSiteId(siteId))
            return Results.BadRequest(new { error = "X-Site-Id requis et invalide." });

        var tenant = context.HttpContext.RequestServices.GetRequiredService<CurrentTenant>();
        if (!string.Equals(siteId, tenant.SiteId, StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { error = "X-Site-Id incohérent avec le jeton Agent." }, statusCode: StatusCodes.Status403Forbidden);

        return await next(context);
    }
}
