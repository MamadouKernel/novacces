using Microsoft.Extensions.Configuration;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Lit le libellé d'un site depuis la configuration ("Sites:{siteId}:Label"),
/// même clé que celle déjà utilisée pour la configuration du poste agent
/// (voir AgentContractEndpoints.cs, GET /api/site/config) — une seule source
/// de vérité pour le nom affiché d'un site, pas une seconde convention.
/// </summary>
public sealed class ConfigurationSiteDisplayNameProvider : ISiteDisplayNameProvider
{
    private readonly IConfiguration _configuration;

    public ConfigurationSiteDisplayNameProvider(IConfiguration configuration) => _configuration = configuration;

    public string GetLabel(string siteId) => _configuration[$"Sites:{siteId}:Label"] ?? siteId;
}
