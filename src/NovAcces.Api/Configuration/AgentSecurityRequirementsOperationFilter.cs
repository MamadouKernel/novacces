using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace NovAcces.Api.Configuration;

/// <summary>
/// Déclare dans Swagger le schéma d'authentification réellement exigé par
/// chaque opération, à partir des métadonnées d'autorisation posées sur
/// l'endpoint (RequireAuthorization). Sans ce filtre, components.securitySchemes
/// restait vide et aucune opération ne déclarait de paramètre d'en-tête — le
/// contrat OpenAPI ne disait rien sur l'authentification (retour du dev app agent,
/// 05/08/2026). La policy "AgentTerminal" exige l'en-tête X-Api-Key sur
/// TOUTE requête (voir AuthSetup.cs), qu'un Bearer soit présent ou non : les
/// deux schémas sont donc déclarés ensemble, jamais l'un sans l'autre, pour
/// cette policy précise.
/// </summary>
public sealed class AgentSecurityRequirementsOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var authData = context.ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<IAuthorizeData>()
            .ToList();

        if (authData.Count == 0)
            return;

        var policies = authData.Select(a => a.Policy).Where(p => !string.IsNullOrEmpty(p)).ToList();
        var requiresApiKey = policies.Contains("AgentTerminal");

        var schemes = new List<string> { "Bearer" };
        if (requiresApiKey)
            schemes.Add("ApiKey");

        var requirement = new OpenApiSecurityRequirement();
        foreach (var scheme in schemes)
        {
            requirement[new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = scheme },
            }] = new List<string>();
        }
        operation.Security.Add(requirement);

        operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Authentification absente ou invalide." });
        operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Authentifié, mais droits insuffisants." });
    }
}
