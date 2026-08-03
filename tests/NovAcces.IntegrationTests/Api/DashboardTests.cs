using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using NovAcces.Shared.Dtos;
using Xunit;

namespace NovAcces.IntegrationTests.Api;

/// <summary>
/// Tests d'intégration du dashboard sûreté : lectures (journal, présents) sous
/// policy RBAC, et surtout la DIFFUSION TEMPS RÉEL — un scan effectué à un poste
/// doit parvenir en direct aux clients SignalR du même site (REQ-F-06).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DashboardTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NovAccesApiFactory _factory;

    public DashboardTests(NovAccesApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task Journal_RequiresDashboardRole()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        // Anonyme -> 401.
        var anon = await _factory.CreateClient().GetAsync("/api/dashboard/journal");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        // Terminal Agent (clé API) -> 403 : l'Agent n'est pas un rôle « Dashboard ».
        var agent = _factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Api-Key", NovAccesApiFactory.TestApiKey);
        var agentResp = await agent.GetAsync("/api/dashboard/journal");
        Assert.Equal(HttpStatusCode.Forbidden, agentResp.StatusCode);

        // Sûreté -> 200.
        var surete = await LoginNewUserAsync("Surete");
        var ok = await surete.GetAsync("/api/dashboard/journal");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [SkippableFact]
    public async Task OnSite_ReflectsCheckedInVisitor()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var visitorName = $"Présent-{Guid.NewGuid():N}";
        await CreateVisitAndCheckInAsync(visitorName);

        var surete = await LoginNewUserAsync("Surete");
        var onSite = await surete.GetFromJsonAsync<List<OnSiteVisitorDto>>("/api/dashboard/on-site", Json);

        Assert.Contains(onSite!, v => v.VisitorName == visitorName);
    }

    [SkippableFact]
    public async Task Scan_BroadcastsLiveEvent_OverSignalR()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var sureteToken = await GetTokenAsync("Surete");

        // Connexion au hub via le serveur de test (transport long-polling, fiable
        // avec TestServer), sur le groupe du site sicopa.
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, $"hubs/scan?site={NovAccesApiFactory.TestSite}"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(sureteToken);
            })
            .Build();

        var received = new TaskCompletionSource<ScanEventDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<ScanEventDto>("ScanRecorded", e => received.TrySetResult(e));
        await hub.StartAsync();

        // Un scan d'entrée : crée une visite (Hôte) puis scanne (Agent).
        var visitorName = $"Live-{Guid.NewGuid():N}";
        await CreateVisitAndCheckInAsync(visitorName);

        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(visitorName, evt.VisitorName);
        Assert.True(evt.IsGranted);
        Assert.Equal("GRANTED", evt.VerdictCode);
    }

    [SkippableFact]
    public async Task Summary_And_CsvExport_Work()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        await CreateVisitAndCheckInAsync($"Synth-{Guid.NewGuid():N}");
        var surete = await LoginNewUserAsync("Surete");

        // Synthèse : au moins le scan que l'on vient de produire aujourd'hui,
        // avec appréciation et recommandation renseignées.
        var summary = await surete.GetFromJsonAsync<DashboardSummaryDto>("/api/dashboard/summary", Json);
        Assert.NotNull(summary);
        Assert.True(summary!.ScansToday >= 1);
        Assert.True(summary.EntriesGranted >= 1);
        Assert.False(string.IsNullOrWhiteSpace(summary.RefusalAppreciation));
        Assert.False(string.IsNullOrWhiteSpace(summary.Recommendation));

        // Courbe d'affluence : 24 tranches horaires, dont la somme = scans du jour.
        Assert.Equal(24, summary.HourlyScans.Count);
        Assert.Equal(summary.ScansToday, summary.HourlyScans.Sum());

        // Export CSV : type et en-tête attendus.
        var csvResp = await surete.GetAsync("/api/dashboard/journal.csv");
        Assert.Equal(HttpStatusCode.OK, csvResp.StatusCode);
        Assert.Equal("text/csv", csvResp.Content.Headers.ContentType!.MediaType);
        var csv = await csvResp.Content.ReadAsStringAsync();
        Assert.StartsWith("Horodatage;Visiteur;Agent;Direction", csv);
    }

    [SkippableFact]
    public async Task Journal_Search_FiltersByVisitorName()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var unique = $"Cherchable{Guid.NewGuid():N}";
        await CreateVisitAndCheckInAsync(unique);

        var surete = await LoginNewUserAsync("Surete");
        var results = await surete.GetFromJsonAsync<PagedResultDto<ScanJournalEntryDto>>(
            $"/api/dashboard/journal?q={unique}", Json);

        Assert.NotEmpty(results!.Items);
        Assert.All(results.Items, r => Assert.Contains(unique, r.VisitorName));
    }

    /// <summary>
    /// Sécurité (OWASP A03) : un nom de visiteur commençant par « = » ne doit
    /// pas être exporté comme une formule exécutable dans le CSV du journal —
    /// il est neutralisé par un préfixe apostrophe.
    /// </summary>
    [SkippableFact]
    public async Task CsvExport_NeutralizesFormulaInjection()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var name = "=CMD" + Guid.NewGuid().ToString("N"); // début de « formule »
        await CreateVisitAndCheckInAsync(name);

        var surete = await LoginNewUserAsync("Surete");
        var csv = await (await surete.GetAsync("/api/dashboard/journal.csv")).Content.ReadAsStringAsync();

        Assert.Contains("'" + name, csv);       // neutralisé (préfixe apostrophe)
        Assert.DoesNotContain(";" + name, csv);  // jamais laissé brut en tête de cellule
    }

    [SkippableFact]
    public async Task ManualCheckOut_RemovesVisitorFromSite_AndIsRestrictedToSecurity()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        // §7 : le visiteur repart sans scanner. Sans sortie manuelle, il
        // resterait « présent » et en dépassement pour toujours.
        var visitorName = $"SansScan-{Guid.NewGuid():N}";
        var visitId = await CreateVisitAndCheckInAsync(visitorName);

        // Un Hôte n'a pas à clore le cycle d'un visiteur : réservé à la sûreté.
        var hote = await LoginNewUserAsync("Hote");
        var refused = await hote.PostAsync($"/api/dashboard/on-site/{visitId}/check-out", null);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var surete = await LoginNewUserAsync("Surete");
        var resp = await surete.PostAsync($"/api/dashboard/on-site/{visitId}/check-out", null);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<ManualCheckOutResponseDto>(Json);
        Assert.Equal(visitorName, result!.VisitorName);

        // Il ne figure plus parmi les présents.
        var onSite = await surete.GetFromJsonAsync<List<OnSiteVisitorDto>>("/api/dashboard/on-site", Json);
        Assert.DoesNotContain(onSite!, v => v.VisitorName == visitorName);

        // La sortie est bien journalisée, et attribuée à la sûreté.
        var journal = await surete.GetFromJsonAsync<PagedResultDto<ScanJournalEntryDto>>(
            $"/api/dashboard/journal?q={visitorName}", Json);
        Assert.Contains(journal!.Items, e => e.WasCheckOut && e.Detail.Contains("manuellement"));

        // Deuxième appel : il n'est plus présent, donc conflit.
        var again = await surete.PostAsync($"/api/dashboard/on-site/{visitId}/check-out", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [SkippableFact]
    public async Task Journal_SearchesByCompanyAndMotif()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        // §9 : la recherche doit porter aussi sur l'entreprise et le motif,
        // qui vivent dans « visits » et non dans le journal (minimisation).
        var visitorName = $"Recherche-{Guid.NewGuid():N}";
        var company = $"Entreprise{Guid.NewGuid():N}"[..20];
        await CreateVisitAndCheckInAsync(visitorName, company);

        var surete = await LoginNewUserAsync("Surete");
        var byCompany = await surete.GetFromJsonAsync<PagedResultDto<ScanJournalEntryDto>>(
            $"/api/dashboard/journal?q={company}", Json);

        Assert.Contains(byCompany!.Items, e => e.VisitorName == visitorName);
    }

    // ---- Aides ----

    private async Task<Guid> CreateVisitAndCheckInAsync(
        string visitorName, string company = "ACME")
    {
        var hote = await LoginNewUserAsync("Hote");
        var createResp = await hote.PostAsJsonAsync("/api/visits", new CreateVisitRequestDto(
            visitorName, company, "Test dashboard", "Unique", DateTimeOffset.UtcNow, 60, null, null));
        createResp.EnsureSuccessStatusCode();
        var created = await createResp.Content.ReadFromJsonAsync<CreateVisitResponseDto>(Json);

        var agent = _factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Api-Key", NovAccesApiFactory.TestApiKey);
        var scan = await agent.PostAsJsonAsync("/api/scan",
            new ScanRequestDto(created!.SignedQrPayload, "Entry", "ignore"));
        scan.EnsureSuccessStatusCode();

        return created.VisitId;
    }

    private async Task<HttpClient> LoginNewUserAsync(string role)
    {
        var token = await GetTokenAsync(role);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private Task<string> GetTokenAsync(string role) =>
        GetTokenForSiteAsync(role, NovAccesApiFactory.TestSite);

    private async Task<string> GetTokenForSiteAsync(string role, string siteId)
    {
        var admin = _factory.CreateClient();
        var adminLogin = await admin.PostAsJsonAsync("/api/auth/login",
            new LoginRequestDto(NovAccesApiFactory.AdminEmail, NovAccesApiFactory.AdminPassword));
        adminLogin.EnsureSuccessStatusCode();
        var adminToken = (await adminLogin.Content.ReadFromJsonAsync<LoginResponseDto>(Json))!.AccessToken;
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@{siteId}.local";
        const string password = "Test!Passw0rd2026";
        var reg = await admin.PostAsJsonAsync("/api/auth/register",
            new RegisterUserRequestDto(email, password, $"{role} Test", role, siteId));
        reg.EnsureSuccessStatusCode();

        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new LoginRequestDto(email, password));
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<LoginResponseDto>(Json))!.AccessToken;
    }

    /// <summary>
    /// CLOISONNEMENT TEMPS RÉEL (CLAUDE.md §7.3) : un utilisateur rattaché à un
    /// AUTRE site ne doit ni s'abonner, ni recevoir le flux de scans de sicopa.
    /// Non-régression de la faille corrigée le 25/07/2026 (ScanEventsHub).
    /// </summary>
    [SkippableFact]
    public async Task Hub_RejectsCrossTenantSubscription()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        // Sûreté rattachée à « sanpedro », qui tente de viser le site sicopa.
        var foreignToken = await GetTokenForSiteAsync("Surete", NovAccesApiFactory.TestSite2);

        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, $"hubs/scan?site={NovAccesApiFactory.TestSite}"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(foreignToken);
            })
            .Build();

        var received = new TaskCompletionSource<ScanEventDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<ScanEventDto>("ScanRecorded", e => received.TrySetResult(e));

        try
        {
            await hub.StartAsync();
        }
        catch
        {
            // Connexion refusée d'emblée = comportement attendu.
            return;
        }

        // Si la connexion a été acceptée puis coupée par Abort(), elle ne doit
        // recevoir AUCUN scan du site sicopa.
        await CreateVisitAndCheckInAsync($"Leak-{Guid.NewGuid():N}");
        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(3)));

        Assert.NotSame(received.Task, completed); // aucun événement inter-tenant reçu
    }
}
