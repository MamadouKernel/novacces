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

    /// <summary>Terminal enrôlé pour DEUX sites (TestSite + TestSite2) — voir NovAccesApiFactory.</summary>
    private HttpClient MultiSiteAgentClient()
    {
        var agent = _factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Api-Key", NovAccesApiFactory.TestApiKeyMultiSite);
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

    [SkippableFact]
    public async Task Scan_WithShiftToken_IsAttributedToAgentMatricule()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var matricule = "SG-" + Guid.NewGuid().ToString("N")[..6];
        const string pin = "4917";

        // 1. L'Admin crée l'agent (matricule + PIN) pour le site.
        var admin = await LoginNewUserAsync("Admin");
        (await admin.PostAsJsonAsync("/api/admin/agents",
            new CreateAgentRequestDto(NovAccesApiFactory.TestSite, matricule, "Agent Test", pin)))
            .EnsureSuccessStatusCode();

        // 2. Prise de poste sur le terminal (clé API) : matricule + PIN → jeton.
        var agent = AgentClient();
        var shiftResp = await agent.PostAsJsonAsync("/api/agent/shift/start", new ShiftStartRequestDto(matricule, pin));
        shiftResp.EnsureSuccessStatusCode();
        var shift = await shiftResp.Content.ReadFromJsonAsync<ShiftStartResponseDto>(Json);
        Assert.Equal(matricule, shift!.Matricule);

        // 3. Une visite + son QR signé.
        var (visitId, payload) = await CreateVisitWithQrAsync($"ScanShift-{Guid.NewGuid():N}");

        // 4. Scan AVEC le jeton de poste → tracé au matricule de l'agent.
        agent.DefaultRequestHeaders.Add("X-Shift-Token", shift.ShiftToken);
        (await agent.PostAsJsonAsync("/api/scan", new ScanRequestDto(payload, "Entry", "ignore")))
            .EnsureSuccessStatusCode();

        Assert.True(await CountScanLogsByAgentAsync(visitId, matricule) >= 1,
            "Le scan aurait dû être tracé au matricule de l'agent.");
    }

    [SkippableFact]
    public async Task TerminalSites_ReturnsSingleSite_ForSingleSiteTerminal()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var sites = await AgentClient().GetFromJsonAsync<List<string>>("/api/agent/sites", Json);
        Assert.Equal(new[] { NovAccesApiFactory.TestSite }, sites);
    }

    [SkippableFact]
    public async Task TerminalSites_ReturnsAllAllowedSites_ForMultiSiteTerminal()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var sites = await MultiSiteAgentClient().GetFromJsonAsync<List<string>>("/api/agent/sites", Json);
        Assert.Equal(
            new[] { NovAccesApiFactory.TestSite, NovAccesApiFactory.TestSite2 }.OrderBy(s => s),
            sites!.OrderBy(s => s));
    }

    [SkippableFact]
    public async Task ShiftStart_MultiSiteTerminal_WithoutSiteHeader_IsRejected()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var matricule = "SG-" + Guid.NewGuid().ToString("N")[..6];
        var admin = await LoginNewUserAsync("Admin");
        (await admin.PostAsJsonAsync("/api/admin/agents",
            new CreateAgentRequestDto(NovAccesApiFactory.TestSite, matricule, "Agent Test", "2468")))
            .EnsureSuccessStatusCode();

        // Terminal multi-sites : sans X-Site-Id, impossible de savoir quel site
        // l'agent a choisi → refusé (jamais un site par défaut deviné).
        var resp = await MultiSiteAgentClient().PostAsJsonAsync(
            "/api/agent/shift/start", new ShiftStartRequestDto(matricule, "2468"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [SkippableFact]
    public async Task ShiftStart_MultiSiteTerminal_WithSiteOutsideAllowList_IsRejected()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var matricule = "SG-" + Guid.NewGuid().ToString("N")[..6];
        var admin = await LoginNewUserAsync("Admin");
        (await admin.PostAsJsonAsync("/api/admin/agents",
            new CreateAgentRequestDto(NovAccesApiFactory.TestSite, matricule, "Agent Test", "2468")))
            .EnsureSuccessStatusCode();

        var agent = MultiSiteAgentClient();
        agent.DefaultRequestHeaders.Add("X-Site-Id", "site-non-autorise-pour-ce-terminal");
        var resp = await agent.PostAsJsonAsync("/api/agent/shift/start", new ShiftStartRequestDto(matricule, "2468"));

        // Un terminal ne peut jamais ouvrir un poste sur un site hors de sa
        // liste autorisée, même avec un matricule/PIN valides ailleurs.
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [SkippableFact]
    public async Task ShiftStart_MultiSiteTerminal_WithAllowedSite_AttributesScanToThatSiteSchema()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var matricule = "SG-" + Guid.NewGuid().ToString("N")[..6];
        const string pin = "3141";

        // L'agent est créé sur TestSite2 : le second site que ce terminal peut
        // servir (pas le premier), pour bien prouver que le choix fait foi.
        var admin = await LoginNewUserAsync("Admin");
        (await admin.PostAsJsonAsync("/api/admin/agents",
            new CreateAgentRequestDto(NovAccesApiFactory.TestSite2, matricule, "Agent Test", pin)))
            .EnsureSuccessStatusCode();

        var agent = MultiSiteAgentClient();
        agent.DefaultRequestHeaders.Add("X-Site-Id", NovAccesApiFactory.TestSite2);

        var shiftResp = await agent.PostAsJsonAsync("/api/agent/shift/start", new ShiftStartRequestDto(matricule, pin));
        shiftResp.EnsureSuccessStatusCode();
        var shift = await shiftResp.Content.ReadFromJsonAsync<ShiftStartResponseDto>(Json);

        var (visitId, payload) = await CreateVisitWithQrAsync(
            $"ScanMultiSite-{Guid.NewGuid():N}", NovAccesApiFactory.TestSite2);

        agent.DefaultRequestHeaders.Add("X-Shift-Token", shift!.ShiftToken);
        (await agent.PostAsJsonAsync("/api/scan", new ScanRequestDto(payload, "Entry", "ignore")))
            .EnsureSuccessStatusCode();

        Assert.True(
            await CountScanLogsByAgentAsync(visitId, matricule, NovAccesApiFactory.TestSite2) >= 1,
            "Le scan aurait dû être tracé dans le schéma du site choisi à la prise de poste (TestSite2), pas TestSite.");
    }

    [SkippableFact]
    public async Task ShiftStart_WithWrongPin_IsRejected()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var matricule = "SG-" + Guid.NewGuid().ToString("N")[..6];
        var admin = await LoginNewUserAsync("Admin");
        (await admin.PostAsJsonAsync("/api/admin/agents",
            new CreateAgentRequestDto(NovAccesApiFactory.TestSite, matricule, "Agent Test", "1111")))
            .EnsureSuccessStatusCode();

        var resp = await AgentClient().PostAsJsonAsync("/api/agent/shift/start",
            new ShiftStartRequestDto(matricule, "9999"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ---- Aides ----

    private async Task<(Guid VisitId, string Payload)> CreateVisitWithQrAsync(
        string visitorName, string site = NovAccesApiFactory.TestSite)
    {
        var hote = await LoginNewUserAsync("Hote", site);
        var resp = await hote.PostAsJsonAsync("/api/visits", new CreateVisitRequestDto(
            visitorName, "ACME", "Test", "Unique", DateTimeOffset.UtcNow, 60, null, null));
        resp.EnsureSuccessStatusCode();
        var created = await resp.Content.ReadFromJsonAsync<CreateVisitResponseDto>(Json);
        return (created!.VisitId, created.SignedQrPayload);
    }

    private static async Task<long> CountScanLogsByAgentAsync(
        Guid visitId, string agentId, string site = NovAccesApiFactory.TestSite)
    {
        await using var conn = new NpgsqlConnection(NovAccesApiFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT count(*) FROM \"site_{site}\".scan_logs " +
            "WHERE \"VisitId\" = @id AND \"AgentId\" = @agent";
        cmd.Parameters.AddWithValue("id", visitId);
        cmd.Parameters.AddWithValue("agent", agentId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

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

    private async Task<HttpClient> LoginNewUserAsync(string role, string site = NovAccesApiFactory.TestSite)
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
            new RegisterUserRequestDto(email, password, $"{role} Test", role, site));
        reg.EnsureSuccessStatusCode();

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(email, password));
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginResponseDto>(Json))!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
