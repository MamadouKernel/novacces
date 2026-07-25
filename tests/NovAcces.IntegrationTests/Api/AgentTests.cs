using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using NovAcces.Shared.Dtos;
using Xunit;

namespace NovAcces.IntegrationTests.Api;

/// <summary>
/// Endpoints de l'application agent (§11 attendus, §6 liste hors-ligne + resync).
/// Authentifiés par clé API de terminal (rôle Agent).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AgentTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NovAccesApiFactory _factory;

    public AgentTests(NovAccesApiFactory factory) => _factory = factory;

    private HttpClient AgentClient()
    {
        var agent = _factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Api-Key", NovAccesApiFactory.TestApiKey);
        return agent;
    }

    [SkippableFact]
    public async Task ExpectedToday_ListsVisitors_ForAgentOnly()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var name = $"Attendu-{Guid.NewGuid():N}";
        await CreateVisitAsync(name);

        var expected = await AgentClient().GetFromJsonAsync<List<ExpectedVisitorDto>>("/api/agent/expected-today", Json);
        Assert.Contains(expected!, e => e.VisitorName == name);

        // Un utilisateur web (Hôte) n'a pas accès à cet endpoint agent.
        var hote = await LoginNewUserAsync("Hote");
        var forbidden = await hote.GetAsync("/api/agent/expected-today");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [SkippableFact]
    public async Task OfflineList_IsSigned()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var list = await AgentClient().GetFromJsonAsync<OfflineListDto>("/api/agent/offline-list", Json);
        Assert.False(string.IsNullOrWhiteSpace(list!.SignedList));
        Assert.True(list.ExpiresAt > list.IssuedAt);
    }

    [SkippableFact]
    public async Task Resync_ReportsConflict_ForScanOfRevokedQr()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var name = $"Resync-{Guid.NewGuid():N}";
        var visitId = await CreateVisitAsync(name);
        var token = await ReadVisitTokenAsync(visitId);

        // Le QR est révoqué (comme depuis le dashboard pendant une coupure).
        var surete = await LoginNewUserAsync("Surete");
        (await surete.PostAsync($"/api/visits/{visitId}/revoke", null)).EnsureSuccessStatusCode();

        // Un scan accordé hors ligne pour ce QR doit remonter en conflit.
        var resync = await AgentClient().PostAsJsonAsync("/api/agent/resync", new ResyncRequestDto(
            new List<OfflineScanDto> { new(token, "Entry", true, DateTimeOffset.UtcNow) }));
        var result = await resync.Content.ReadFromJsonAsync<ResyncResultDto>(Json);

        Assert.Equal(1, result!.Processed);
        Assert.Contains(result.Conflicts, c => c.VisitToken == token);
    }

    [SkippableFact]
    public async Task Resync_JournalsEveryOfflineScan_NotOnlyConflicts()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var name = $"ResyncJournal-{Guid.NewGuid():N}";
        var visitId = await CreateVisitAsync(name);
        var token = await ReadVisitTokenAsync(visitId);

        // QR valide (non révoqué) : un scan accordé + un scan refusé hors ligne.
        // Aucun conflit attendu, mais LES DEUX doivent être journalisés (§6.2, REQ-F-07).
        var resync = await AgentClient().PostAsJsonAsync("/api/agent/resync", new ResyncRequestDto(
            new List<OfflineScanDto>
            {
                new(token, "Entry", true, DateTimeOffset.UtcNow, "Recognized", false),
                new(token, "Entry", false, DateTimeOffset.UtcNow, "TooLate", true),
            }));
        var result = await resync.Content.ReadFromJsonAsync<ResyncResultDto>(Json);

        Assert.Equal(2, result!.Processed);
        Assert.Empty(result.Conflicts);

        var journaled = await CountDegradedScanLogsAsync(visitId);
        Assert.True(journaled >= 2, $"Attendu >= 2 entrées de journal en mode dégradé, obtenu {journaled}.");
    }

    // ---- Aides ----

    private static async Task<long> CountDegradedScanLogsAsync(Guid visitId)
    {
        await using var conn = new NpgsqlConnection(NovAccesApiFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT count(*) FROM \"site_{NovAccesApiFactory.TestSite}\".scan_logs " +
            "WHERE \"VisitId\" = @id AND \"RecordedInDegradedMode\" = true";
        cmd.Parameters.AddWithValue("id", visitId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<Guid> CreateVisitAsync(string visitorName)
    {
        var hote = await LoginNewUserAsync("Hote");
        var resp = await hote.PostAsJsonAsync("/api/visits", new CreateVisitRequestDto(
            visitorName, "ACME", "Test", "Unique", DateTimeOffset.UtcNow, 60, null, null));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<CreateVisitResponseDto>(Json))!.VisitId;
    }

    private static async Task<Guid> ReadVisitTokenAsync(Guid visitId)
    {
        await using var conn = new NpgsqlConnection(NovAccesApiFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT \"VisitToken\" FROM \"site_{NovAccesApiFactory.TestSite}\".visits WHERE \"Id\" = @id";
        cmd.Parameters.AddWithValue("id", visitId);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
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
