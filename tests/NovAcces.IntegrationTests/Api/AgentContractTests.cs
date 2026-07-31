using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using NovAcces.Shared.Dtos;
using Xunit;

namespace NovAcces.IntegrationTests.Api;

[Collection(ApiCollection.Name)]
public sealed class AgentContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NovAccesApiFactory _factory;

    public AgentContractTests(NovAccesApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task ContractHealthAndPublicKey_AreAvailable()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);
        var client = _factory.CreateClient();

        var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var healthBody = await health.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("ok", healthBody.GetProperty("status").GetString());
        Assert.True(healthBody.TryGetProperty("serverTimeUtc", out _));

        var key = await client.GetFromJsonAsync<PublicKeyDto>("/api/keys/public", Json);
        Assert.NotNull(key);
        Assert.False(string.IsNullOrWhiteSpace(key!.PublicKeyPem));
    }

    [SkippableFact]
    public async Task WebLogin_RefreshRotates_AndLogoutRevokes()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);
        var client = _factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequestDto(NovAccesApiFactory.AdminEmail, NovAccesApiFactory.AdminPassword));
        login.EnsureSuccessStatusCode();
        var first = await login.Content.ReadFromJsonAsync<LoginResponseDto>(Json);
        Assert.False(string.IsNullOrWhiteSpace(first!.RefreshToken));

        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequestDto(first.RefreshToken!));
        refresh.EnsureSuccessStatusCode();
        var second = await refresh.Content.ReadFromJsonAsync<LoginResponseDto>(Json);
        Assert.NotEqual(first.RefreshToken, second!.RefreshToken);

        (await client.PostAsJsonAsync("/api/auth/logout", new RefreshTokenRequestDto(second.RefreshToken!)))
            .EnsureSuccessStatusCode();
        var replay = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequestDto(second.RefreshToken!));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [SkippableFact]
    public async Task AgentLogin_UsesContractPayload_AndCanReadSiteConfig()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var matricule = "SG-C-" + Guid.NewGuid().ToString("N")[..8];
        const string pin = "4815";
        var admin = await LoginAdminAsync();
        (await admin.PostAsJsonAsync("/api/admin/agents",
            new CreateAgentRequestDto(NovAccesApiFactory.TestSite, matricule, "Agent Contrat", pin)))
            .EnsureSuccessStatusCode();

        var agent = _factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Site-Id", NovAccesApiFactory.TestSite);
        agent.DefaultRequestHeaders.Add("X-Api-Key", NovAccesApiFactory.TestApiKey);
        var request = new LoginRequestDto(null, null)
        {
            Matricule = matricule,
            MotDePasse = pin,
        };
        var login = await agent.PostAsJsonAsync("/api/auth/login", request);
        login.EnsureSuccessStatusCode();
        var response = await login.Content.ReadFromJsonAsync<AgentLoginResponseDto>(Json);
        Assert.Equal(matricule, response!.Agent.Matricule);
        Assert.False(string.IsNullOrWhiteSpace(response.AccessToken));

        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", response.AccessToken);
        var config = await agent.GetFromJsonAsync<SiteConfigDto>("/api/site/config", Json);
        Assert.NotNull(config);
        Assert.Contains(config!.Postes, p => p.Id == "entry");
    }

    [SkippableFact]
    public async Task ContractOfflineList_ContainsSignedProjectionAndFourHourMaximumTtl()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var visitorName = $"OfflineContract-{Guid.NewGuid():N}";
        var hote = await LoginNewUserAsync("Hote");
        (await hote.PostAsJsonAsync("/api/visits", new CreateVisitRequestDto(
            visitorName, "ACME", "Contrat", "Unique", DateTimeOffset.UtcNow, 60, null, null)))
            .EnsureSuccessStatusCode();

        var agent = await LoginAgentAsync();
        var list = await agent.GetFromJsonAsync<ContractOfflineListDto>("/api/offline-list", Json);

        Assert.NotNull(list);
        Assert.False(string.IsNullOrWhiteSpace(list!.SignedList));
        Assert.True(list.ExpiresAtUtc > list.GeneratedAtUtc);
        Assert.True(list.ExpiresAtUtc - list.GeneratedAtUtc <= TimeSpan.FromHours(4));
        Assert.Contains(list.Visits, v => v.Nom == visitorName && v.Statut == "Valid");
    }

    [SkippableFact]
    public async Task ContractSync_ConsumesArrayAndReturns409ForOfflineGrantAgainstRevokedQr()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var visitorName = $"SyncContract-{Guid.NewGuid():N}";
        var hote = await LoginNewUserAsync("Hote");
        var createdResponse = await hote.PostAsJsonAsync("/api/visits", new CreateVisitRequestDto(
            visitorName, "ACME", "Contrat", "Unique", DateTimeOffset.UtcNow, 60, null, null));
        createdResponse.EnsureSuccessStatusCode();
        var created = await createdResponse.Content.ReadFromJsonAsync<CreateVisitResponseDto>(Json);

        var surete = await LoginNewUserAsync("Surete");
        (await surete.PostAsync($"/api/visits/{created!.VisitId}/revoke", null)).EnsureSuccessStatusCode();

        var agent = await LoginAgentAsync();
        var sync = await agent.PostAsJsonAsync("/api/scan/sync", new[]
        {
            new ContractOfflineScanDto(
                created.SignedQrPayload, "Entry", "SG-CONTRACT", DateTimeOffset.UtcNow, "GRANTED")
        });

        Assert.Equal(HttpStatusCode.Conflict, sync.StatusCode);
        var result = await sync.Content.ReadFromJsonAsync<ContractScanSyncResponse>(Json);
        Assert.NotNull(result);
        Assert.Contains(result!.Conflicts, c => c.VisitId == created.VisitId);
    }

    [SkippableFact]
    public async Task ContractAgentRoutes_RequireSiteHeaderWithBearerToken()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var agent = await LoginAgentAsync();
        agent.DefaultRequestHeaders.Remove("X-Site-Id");
        var response = await agent.GetAsync("/api/site/config");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task ContractHub_AllowsAgentAndPublishesVisitCreated()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var agent = await LoginAgentAsync();
        var accessToken = agent.DefaultRequestHeaders.Authorization!.Parameter!;
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress,
                $"hubs/scan-events?site={NovAccesApiFactory.TestSite}"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .Build();

        var received = new TaskCompletionSource<AgentVisitEventDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<AgentVisitEventDto>("VisitCreated", e => received.TrySetResult(e));
        await hub.StartAsync();

        var visitorName = $"HubContract-{Guid.NewGuid():N}";
        var hote = await LoginNewUserAsync("Hote");
        var createdResponse = await hote.PostAsJsonAsync("/api/visits", new CreateVisitRequestDto(
            visitorName, "ACME", "Contrat", "Unique", DateTimeOffset.UtcNow, 60, null, null));
        createdResponse.EnsureSuccessStatusCode();
        var created = await createdResponse.Content.ReadFromJsonAsync<CreateVisitResponseDto>(Json);

        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(created!.VisitId, evt.VisitId);
        Assert.Equal(visitorName, evt.VisitorName);
    }

    private async Task<HttpClient> LoginAdminAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequestDto(NovAccesApiFactory.AdminEmail, NovAccesApiFactory.AdminPassword));
        login.EnsureSuccessStatusCode();
        var response = await login.Content.ReadFromJsonAsync<LoginResponseDto>(Json);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", response!.AccessToken);
        return client;
    }

    private async Task<HttpClient> LoginNewUserAsync(string role)
    {
        var admin = await LoginAdminAsync();
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@sicopa.local";
        const string password = "Test!Passw0rd2026";
        (await admin.PostAsJsonAsync("/api/auth/register",
            new RegisterUserRequestDto(email, password, $"{role} Contract", role, NovAccesApiFactory.TestSite)))
            .EnsureSuccessStatusCode();

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(email, password));
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginResponseDto>(Json))!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<HttpClient> LoginAgentAsync()
    {
        var admin = await LoginAdminAsync();
        var matricule = "SG-CONTRACT-" + Guid.NewGuid().ToString("N")[..8];
        const string pin = "4815";
        (await admin.PostAsJsonAsync("/api/admin/agents",
            new CreateAgentRequestDto(NovAccesApiFactory.TestSite, matricule, "Agent Contrat", pin)))
            .EnsureSuccessStatusCode();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Site-Id", NovAccesApiFactory.TestSite);
        client.DefaultRequestHeaders.Add("X-Api-Key", NovAccesApiFactory.TestApiKey);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(null, null)
        {
            Matricule = matricule,
            MotDePasse = pin,
        });
        login.EnsureSuccessStatusCode();
        var response = await login.Content.ReadFromJsonAsync<AgentLoginResponseDto>(Json);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", response!.AccessToken);
        return client;
    }
}
