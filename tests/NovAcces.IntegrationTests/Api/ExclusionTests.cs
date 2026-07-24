using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NovAcces.Shared.Dtos;
using Xunit;

namespace NovAcces.IntegrationTests.Api;

/// <summary>
/// Liste d'exclusion (REQ-F-11) : une personne exclue se voit refuser toute
/// nouvelle demande (refus générique au scan), la comparaison ignorant casse et
/// accents ; la gestion est réservée à la Sûreté/Admin.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ExclusionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NovAccesApiFactory _factory;

    public ExclusionTests(NovAccesApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task ExcludedVisitor_IsDenied_AtScan_IgnoringCaseAndAccents()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var token = Guid.NewGuid().ToString("N")[..8];
        var excludedDisplay = $"Éxclu Test {token}";     // avec accent + majuscules
        var visitName = $"exclu test {token}";           // même personne, normalisée

        // La sûreté ajoute la personne à la liste d'exclusion.
        var surete = await LoginNewUserAsync("Surete");
        var add = await surete.PostAsJsonAsync("/api/exclusions",
            new AddExclusionRequestDto(excludedDisplay, "Test d'intégration"));
        add.EnsureSuccessStatusCode();

        // L'hôte crée une demande pour cette personne (nom écrit différemment).
        var hote = await LoginNewUserAsync("Hote");
        var create = await hote.PostAsJsonAsync("/api/visits", new CreateVisitRequestDto(
            visitName, "ACME", "Test", "Unique", DateTimeOffset.UtcNow, 60, null, null));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<CreateVisitResponseDto>(Json);

        // Au scan, l'agent reçoit un refus (exclusion), pas un accès.
        var agent = _factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Api-Key", NovAccesApiFactory.TestApiKey);
        var scanResp = await agent.PostAsJsonAsync("/api/scan",
            new ScanRequestDto(created!.SignedQrPayload, "Entry", "ignore"));
        var scan = await scanResp.Content.ReadFromJsonAsync<ScanResponseDto>(Json);

        Assert.False(scan!.IsGranted);
        Assert.Equal("DENIED_Excluded", scan.VerdictCode);
    }

    [SkippableFact]
    public async Task ExclusionManagement_ForbiddenForHote()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var hote = await LoginNewUserAsync("Hote");
        var resp = await hote.GetAsync("/api/exclusions");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    private async Task<HttpClient> LoginNewUserAsync(string role)
    {
        var admin = _factory.CreateClient();
        var adminLogin = await admin.PostAsJsonAsync("/api/auth/login",
            new LoginRequestDto(NovAccesApiFactory.AdminEmail, NovAccesApiFactory.AdminPassword));
        adminLogin.EnsureSuccessStatusCode();
        var adminToken = (await adminLogin.Content.ReadFromJsonAsync<LoginResponseDto>(Json))!.AccessToken;
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@sicopa.local";
        const string password = "Test!Passw0rd2026";
        var reg = await admin.PostAsJsonAsync("/api/auth/register",
            new RegisterUserRequestDto(email, password, $"{role} Test", role, NovAccesApiFactory.TestSite));
        reg.EnsureSuccessStatusCode();

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(email, password));
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginResponseDto>(Json))!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
