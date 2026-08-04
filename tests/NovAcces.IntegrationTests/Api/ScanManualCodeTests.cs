using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NovAcces.Shared.Dtos;
using Xunit;

namespace NovAcces.IntegrationTests.Api;

/// <summary>
/// Code de secours (alternative au QR) de bout en bout via l'API réelle :
/// POST /api/visits expose le code brut à la création, POST /api/scan/manual-code
/// applique EXACTEMENT les mêmes règles de sûreté que le scan QR (anti-rejeu,
/// liste d'exclusion relue en direct, cycle entrée/sortie directionnel) —
/// puisque les deux chemins partagent ScanExecutionCore. Miroir de
/// ExclusionTests.cs et de la matrice de VisitsTests.cs, mais par code.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ScanManualCodeTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NovAccesApiFactory _factory;

    public ScanManualCodeTests(NovAccesApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task ManualCode_GrantsEntry_ThenExit_ThenClosedCycleIsRejected()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var hote = await LoginNewUserAsync("Hote");
        var name = $"Code Secours {Guid.NewGuid():N}"[..24];
        var create = await hote.PostAsJsonAsync("/api/visits", new CreateVisitRequestDto(
            name, "ACME", "Test", "Unique", DateTimeOffset.UtcNow, 60, null, null));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<CreateVisitResponseDto>(Json);

        // Le code brut n'existe que dans cette réponse (seule son empreinte
        // est persistée) — pas de porte dérobée de test pour le retrouver.
        Assert.False(string.IsNullOrWhiteSpace(created!.ManualCode));

        var agent = _factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Api-Key", NovAccesApiFactory.TestApiKey);

        var entryResp = await agent.PostAsJsonAsync("/api/scan/manual-code",
            new ScanManualCodeRequestDto(created.ManualCode!, "Entry"));
        var entry = await entryResp.Content.ReadFromJsonAsync<ScanResponseDto>(Json);
        Assert.True(entry!.IsGranted);
        Assert.Equal("GRANTED", entry.VerdictCode);

        var exitResp = await agent.PostAsJsonAsync("/api/scan/manual-code",
            new ScanManualCodeRequestDto(created.ManualCode!, "Exit"));
        var exit = await exitResp.Content.ReadFromJsonAsync<ScanResponseDto>(Json);
        Assert.True(exit!.IsGranted);
        Assert.True(exit.IsCheckOut);

        // Mode Unique, cycle déjà bouclé : une nouvelle présentation du MÊME
        // code à l'entrée doit être refusée (anti-rejeu), jamais réadmise.
        var replayResp = await agent.PostAsJsonAsync("/api/scan/manual-code",
            new ScanManualCodeRequestDto(created.ManualCode!, "Entry"));
        var replay = await replayResp.Content.ReadFromJsonAsync<ScanResponseDto>(Json);
        Assert.False(replay!.IsGranted);
        Assert.Equal("DENIED_CycleAlreadyClosed", replay.VerdictCode);
    }

    [SkippableFact]
    public async Task ManualCode_Unknown_IsDeniedAsInvalidCode()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var agent = _factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Api-Key", NovAccesApiFactory.TestApiKey);

        var resp = await agent.PostAsJsonAsync("/api/scan/manual-code",
            new ScanManualCodeRequestDto("ZZZZ-0000", "Entry"));
        var result = await resp.Content.ReadFromJsonAsync<ScanResponseDto>(Json);

        Assert.False(result!.IsGranted);
        Assert.Equal("INVALID_CODE", result.VerdictCode);
    }

    [SkippableFact]
    public async Task ManualCode_VisitorPutOnExclusionListAfterCodeIssued_IsDenied()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var token = Guid.NewGuid().ToString("N")[..8];
        var excludedDisplay = $"Éxclu Code {token}";
        var visitName = $"exclu code {token}";

        var surete = await LoginNewUserAsync("Surete");
        var add = await surete.PostAsJsonAsync("/api/exclusions",
            new AddExclusionRequestDto(excludedDisplay, "Test d'intégration — code de secours"));
        add.EnsureSuccessStatusCode();

        var hote = await LoginNewUserAsync("Hote");
        var create = await hote.PostAsJsonAsync("/api/visits", new CreateVisitRequestDto(
            visitName, "ACME", "Test", "Unique", DateTimeOffset.UtcNow, 60, null, null));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<CreateVisitResponseDto>(Json);

        var agent = _factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Api-Key", NovAccesApiFactory.TestApiKey);
        var scanResp = await agent.PostAsJsonAsync("/api/scan/manual-code",
            new ScanManualCodeRequestDto(created!.ManualCode!, "Entry"));
        var scan = await scanResp.Content.ReadFromJsonAsync<ScanResponseDto>(Json);

        Assert.False(scan!.IsGranted);
        Assert.Equal("DENIED_Excluded", scan.VerdictCode);
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
