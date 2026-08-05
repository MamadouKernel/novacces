using System.Net;
using System.Text.Json;
using Xunit;

namespace NovAcces.IntegrationTests.Api;

/// <summary>
/// Le document Swagger doit se générer sans exception (AgentSecurityRequirementsOperationFilter
/// notamment) et déclarer réellement les schémas d'authentification — retour
/// du dev app agent (05/08/2026) : components.securitySchemes vide, 0 en-tête
/// déclaré sur 80 opérations.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SwaggerTests
{
    private readonly NovAccesApiFactory _factory;

    public SwaggerTests(NovAccesApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task SwaggerDocument_GeneratesSuccessfully_WithBearerAndApiKeySchemes()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schemes = doc.RootElement.GetProperty("components").GetProperty("securitySchemes");
        Assert.True(schemes.TryGetProperty("Bearer", out _));
        Assert.True(schemes.TryGetProperty("ApiKey", out _));

        var paths = doc.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/agent/shift/end", out _),
            "POST /api/agent/shift/end devrait apparaître dans le contrat OpenAPI.");

        // L'opération de scan exige AgentTerminal (Bearer + ApiKey ensemble) —
        // c'est précisément ce que le dev app agent n'arrivait pas à déduire du
        // contrat (Q2).
        var scanSecurity = paths.GetProperty("/api/scan").GetProperty("post").GetProperty("security");
        var declaredSchemes = scanSecurity.EnumerateArray()
            .SelectMany(req => req.EnumerateObject().Select(p => p.Name))
            .ToHashSet();
        Assert.Contains("Bearer", declaredSchemes);
        Assert.Contains("ApiKey", declaredSchemes);
    }
}
