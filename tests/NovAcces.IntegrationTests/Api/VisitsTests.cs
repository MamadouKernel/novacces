using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NovAcces.Shared.Dtos;
using Xunit;

namespace NovAcces.IntegrationTests.Api;

/// <summary>
/// Portail hôte : liste de ses propres demandes et révocation avec moindre
/// privilège (REQ-F-09, section 8.5 du CDC).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class VisitsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NovAccesApiFactory _factory;

    public VisitsTests(NovAccesApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task MyVisits_ReturnsOnlyOwnVisits()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var hoteA = await LoginNewUserAsync("Hote");
        var hoteB = await LoginNewUserAsync("Hote");

        var nameA = $"A-{Guid.NewGuid():N}";
        var nameB = $"B-{Guid.NewGuid():N}";
        await CreateVisitAsync(hoteA, nameA);
        await CreateVisitAsync(hoteB, nameB);

        var mineA = await hoteA.GetFromJsonAsync<List<HostVisitDto>>("/api/visits/mine", Json);

        Assert.Contains(mineA!, v => v.VisitorName == nameA);
        Assert.DoesNotContain(mineA!, v => v.VisitorName == nameB);
    }

    [SkippableFact]
    public async Task Revoke_OwnVisit_Succeeds_AndShowsRevoked()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var hote = await LoginNewUserAsync("Hote");
        var visitId = await CreateVisitAsync(hote, $"Own-{Guid.NewGuid():N}");

        var revoke = await hote.PostAsync($"/api/visits/{visitId}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var mine = await hote.GetFromJsonAsync<List<HostVisitDto>>("/api/visits/mine", Json);
        Assert.Equal("Revoked", mine!.Single(v => v.VisitId == visitId).Status);
    }

    [SkippableFact]
    public async Task Revoke_OthersVisit_AsHote_Is403()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var owner = await LoginNewUserAsync("Hote");
        var other = await LoginNewUserAsync("Hote");
        var visitId = await CreateVisitAsync(owner, $"Owned-{Guid.NewGuid():N}");

        var revoke = await other.PostAsync($"/api/visits/{visitId}/revoke", null);
        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
    }

    [SkippableFact]
    public async Task Revoke_AnyVisit_AsSurete_Succeeds()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var hote = await LoginNewUserAsync("Hote");
        var surete = await LoginNewUserAsync("Surete");
        var visitId = await CreateVisitAsync(hote, $"BySurete-{Guid.NewGuid():N}");

        var revoke = await surete.PostAsync($"/api/visits/{visitId}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
    }

    [SkippableFact]
    public async Task CreatingSecondActiveVisit_ForSameVisitor_Is409()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var hote = await LoginNewUserAsync("Hote");
        var name = $"Doublon-{Guid.NewGuid():N}";

        await CreateVisitAsync(hote, name); // 1re demande active : OK

        var second = await hote.PostAsJsonAsync("/api/visits", new CreateVisitRequestDto(
            name, "ACME", "Test", "Unique", DateTimeOffset.UtcNow, 60, null, null));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [SkippableFact]
    public async Task KnownVisitors_ReturnsPrefillData()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var hote = await LoginNewUserAsync("Hote");
        var name = $"Connu-{Guid.NewGuid():N}";
        var company = $"Sté-{Guid.NewGuid():N}";

        var create = await hote.PostAsJsonAsync("/api/visits", new CreateVisitRequestDto(
            name, company, "Motif habituel", "Unique", DateTimeOffset.UtcNow, 90, null, null));
        create.EnsureSuccessStatusCode();

        var known = await hote.GetFromJsonAsync<List<KnownVisitorDto>>("/api/visits/known-visitors", Json);
        var entry = known!.FirstOrDefault(k => k.Name == name);

        Assert.NotNull(entry);
        Assert.Equal(company, entry!.Company);
        Assert.Equal("Motif habituel", entry.Motif);
        Assert.Equal(90, entry.PlannedDurationMinutes);
    }

    [SkippableFact]
    public async Task BulkCreate_GeneratesQrPerVisitor_AndReportsFailuresPerItem()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var hote = await LoginNewUserAsync("Hote");
        var n1 = $"Grp1-{Guid.NewGuid():N}";
        var n2 = $"Grp2-{Guid.NewGuid():N}";

        static CreateVisitRequestDto Item(string name) =>
            new(name, "ACME", "Groupe", "ThirtyDays", null, 60, null, null);

        var req = new BulkCreateVisitsRequestDto(new[]
        {
            Item(n1),
            Item(n2),
            Item(n1),      // doublon dans le lot
            Item("   "),   // nom vide
        });

        var resp = await hote.PostAsJsonAsync("/api/visits/bulk", req);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<BulkCreateVisitsResponseDto>(Json);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Created);
        Assert.Equal(2, result.Failed);

        // Un QR signé est bien généré pour chaque visiteur valide.
        var ok = result.Items.Single(i => i.VisitorName == n1 && i.Success);
        Assert.False(string.IsNullOrEmpty(ok.SignedQrPayload));

        // Le doublon et le nom vide sont refusés, isolément.
        Assert.Contains(result.Items, i => i.VisitorName == n1 && !i.Success && i.Error!.Contains("déjà"));
        Assert.Contains(result.Items, i => !i.Success && i.Error!.Contains("Nom"));
    }

    [SkippableFact]
    public async Task BulkCreate_EmptyList_Is400()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var hote = await LoginNewUserAsync("Hote");
        var resp = await hote.PostAsJsonAsync("/api/visits/bulk",
            new BulkCreateVisitsRequestDto(Array.Empty<CreateVisitRequestDto>()));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---- Aides ----

    private static async Task<Guid> CreateVisitAsync(HttpClient hote, string visitorName)
    {
        var resp = await hote.PostAsJsonAsync("/api/visits", new CreateVisitRequestDto(
            visitorName, "ACME", "Test", "Unique", DateTimeOffset.UtcNow, 60, null, null));
        resp.EnsureSuccessStatusCode();
        var created = await resp.Content.ReadFromJsonAsync<CreateVisitResponseDto>(Json);
        return created!.VisitId;
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
