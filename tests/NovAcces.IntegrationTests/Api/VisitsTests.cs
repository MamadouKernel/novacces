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
    public async Task SameVisitorName_DifferentCompanies_BothSucceed()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        // Homonymie : deux personnes réelles différentes peuvent partager le
        // même nom. Le garde-fou anti-doublon est porté sur nom + société,
        // donc deux sociétés différentes ne doivent JAMAIS se bloquer.
        var hote = await LoginNewUserAsync("Hote");
        var name = $"Homonyme-{Guid.NewGuid():N}";

        var first = await hote.PostAsJsonAsync("/api/visits", new CreateVisitRequestDto(
            name, "Société A", "Test", "Unique", DateTimeOffset.UtcNow, 60, null, null));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await hote.PostAsJsonAsync("/api/visits", new CreateVisitRequestDto(
            name, "Société B", "Test", "Unique", DateTimeOffset.UtcNow, 60, null, null));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [SkippableFact]
    public async Task ConcurrentCreate_ForSameVisitorAndCompany_OnlyOneSucceeds()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        // Condition de course : la vérification applicative amont ("lire puis
        // écrire") ne suffit pas seule à empêcher deux créations strictement
        // concurrentes du même visiteur. C'est l'index unique partiel en base
        // (IX_visits_ActiveVisitorKey) qui tranche — exactement une des deux
        // requêtes doit réussir, l'autre doit recevoir 409.
        var hote = await LoginNewUserAsync("Hote");
        var name = $"Course-{Guid.NewGuid():N}";
        var request = new CreateVisitRequestDto(
            name, "Société Concurrente", "Test", "Unique", DateTimeOffset.UtcNow, 60, null, null);

        var responses = await Task.WhenAll(
            hote.PostAsJsonAsync("/api/visits", request),
            hote.PostAsJsonAsync("/api/visits", request));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
    }

    [SkippableFact]
    public async Task Create_ForVisitorWithExpiredNeverScannedPriorVisit_Succeeds()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        // Régression du 08/08/2026 : une demande jamais scannée dont la
        // fenêtre (-20/+15 min) est dépassée restait "Valid" en base pour
        // toujours, bloquant à tort une réinvitation du même visiteur avec
        // 409 ("Une demande active existe déjà pour ce visiteur.") alors que
        // l'ancienne ne pourra plus jamais être présentée avec succès.
        var hote = await LoginNewUserAsync("Hote");
        var name = $"Expiree-{Guid.NewGuid():N}";
        var company = "Société Expirée";

        var firstRequest = new CreateVisitRequestDto(
            name, company, "Test", "Unique", DateTimeOffset.UtcNow.AddMinutes(-30), 60, null, null);
        var first = await hote.PostAsJsonAsync("/api/visits", firstRequest);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var secondRequest = new CreateVisitRequestDto(
            name, company, "Test", "Unique", DateTimeOffset.UtcNow.AddHours(1), 60, null, null);
        var second = await hote.PostAsJsonAsync("/api/visits", secondRequest);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
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

    [SkippableFact]
    public async Task History_ReturnsOrderedLifecycle_AndRespectsPrivilege()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var owner = await LoginNewUserAsync("Hote");
        var name = $"Hist-{Guid.NewGuid():N}";
        var visitId = await CreateVisitAsync(owner, name);

        // Le propriétaire voit la chronologie (au moins « créée »), ordonnée.
        var history = await owner.GetFromJsonAsync<VisitHistoryDto>($"/api/visits/{visitId}/history", Json);
        Assert.NotNull(history);
        Assert.Equal(name, history!.VisitorName);
        Assert.Contains(history.Events, e => e.Kind == "created");
        var times = history.Events.Select(e => e.At).ToList();
        Assert.Equal(times.OrderBy(t => t), times);

        // Un autre hôte ne peut pas voir cette demande (moindre privilège) -> 403.
        var other = await LoginNewUserAsync("Hote");
        var forbidden = await other.GetAsync($"/api/visits/{visitId}/history");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        // La Sûreté voit la chronologie de n'importe quelle demande -> 200.
        var surete = await LoginNewUserAsync("Surete");
        var ok = await surete.GetAsync($"/api/visits/{visitId}/history");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [SkippableFact]
    public async Task ReissueQr_ForOwner_ReturnsWorkingPayload()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        // Cas d'usage réel : le visiteur a perdu le message WhatsApp/email
        // avant de se présenter. L'hôte doit pouvoir réafficher le badge —
        // et ce badge doit rester un QR RÉELLEMENT utilisable au poste, pas
        // une simple image de courtoisie.
        var hote = await LoginNewUserAsync("Hote");
        var visitId = await CreateVisitAsync(hote, $"Reissue-{Guid.NewGuid():N}");

        var resp = await hote.GetAsync($"/api/visits/{visitId}/qr");
        resp.EnsureSuccessStatusCode();
        var reissued = await resp.Content.ReadFromJsonAsync<CreateVisitResponseDto>(Json);
        Assert.Equal(visitId, reissued!.VisitId);
        Assert.False(string.IsNullOrWhiteSpace(reissued.SignedQrPayload));

        var agent = _factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Api-Key", NovAccesApiFactory.TestApiKey);
        var scan = await agent.PostAsJsonAsync("/api/scan",
            new ScanRequestDto(reissued.SignedQrPayload, "Entry", "ignore"));
        scan.EnsureSuccessStatusCode();
        var scanResult = await scan.Content.ReadFromJsonAsync<ScanResponseDto>(Json);
        Assert.True(scanResult!.IsGranted);
    }

    [SkippableFact]
    public async Task ReissueQr_ForOthersVisit_AsHote_Is403()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var owner = await LoginNewUserAsync("Hote");
        var other = await LoginNewUserAsync("Hote");
        var visitId = await CreateVisitAsync(owner, $"ReissueOwned-{Guid.NewGuid():N}");

        var resp = await other.GetAsync($"/api/visits/{visitId}/qr");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [SkippableFact]
    public async Task ReissueQr_ForRevokedVisit_Is409()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var hote = await LoginNewUserAsync("Hote");
        var visitId = await CreateVisitAsync(hote, $"ReissueRevoked-{Guid.NewGuid():N}");
        (await hote.PostAsync($"/api/visits/{visitId}/revoke", null)).EnsureSuccessStatusCode();

        var resp = await hote.GetAsync($"/api/visits/{visitId}/qr");
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [SkippableFact]
    public async Task ReissueQr_AsSurete_Succeeds()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var hote = await LoginNewUserAsync("Hote");
        var surete = await LoginNewUserAsync("Surete");
        var visitId = await CreateVisitAsync(hote, $"ReissueSurete-{Guid.NewGuid():N}");

        var resp = await surete.GetAsync($"/api/visits/{visitId}/qr");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
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
