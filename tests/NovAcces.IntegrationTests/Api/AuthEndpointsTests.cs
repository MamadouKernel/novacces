using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using NovAcces.Shared.Dtos;
using Xunit;

namespace NovAcces.IntegrationTests.Api;

/// <summary>
/// Tests d'intégration HTTP du pipeline d'authentification / RBAC / tenant / 2FA,
/// contre l'API réelle démarrée en mémoire. Verrouillent les garanties de
/// sécurité les plus critiques contre toute régression future.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuthEndpointsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NovAccesApiFactory _factory;

    public AuthEndpointsTests(NovAccesApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task Anonymous_OnProtectedEndpoint_Is401()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/visits", SampleVisit());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Hote_OnAgentOnlyEndpoint_Is403()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);
        var client = _factory.CreateClient();
        var hote = await CreateAndLoginAsync(client, "Hote");

        SetBearer(client, hote.AccessToken);
        var response = await client.PostAsJsonAsync("/api/scan",
            new ScanRequestDto("payload-bidon", "Entry", "ignore"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task Hote_CreatesVisit_FromClaimTenant_ThenAgent_Scans_Granted()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);
        var client = _factory.CreateClient();
        var hote = await CreateAndLoginAsync(client, "Hote");
        Assert.Equal(NovAccesApiFactory.TestSite, hote.SiteId);

        // L'Hôte crée une visite SANS en-tête X-Site-Id : le tenant vient du claim.
        SetBearer(client, hote.AccessToken);
        var createResp = await client.PostAsJsonAsync("/api/visits", SampleVisit());
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<CreateVisitResponseDto>(Json);
        Assert.NotNull(created);

        // L'agent (clé API) scanne à l'entrée : accès accordé.
        var agent = _factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Api-Key", NovAccesApiFactory.TestApiKey);
        var scanResp = await agent.PostAsJsonAsync("/api/scan",
            new ScanRequestDto(created!.SignedQrPayload, "Entry", "ignore"));
        Assert.Equal(HttpStatusCode.OK, scanResp.StatusCode);
        var scan = await scanResp.Content.ReadFromJsonAsync<ScanResponseDto>(Json);
        Assert.True(scan!.IsGranted);
        Assert.Equal("GRANTED", scan.VerdictCode);
    }

    [SkippableFact]
    public async Task Hote_TargetingAnotherSite_ViaHeader_Is403()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);
        var client = _factory.CreateClient();
        var hote = await CreateAndLoginAsync(client, "Hote");

        SetBearer(client, hote.AccessToken);
        client.DefaultRequestHeaders.Add("X-Site-Id", "unautresite");
        var response = await client.PostAsJsonAsync("/api/visits", SampleVisit());

        // Un jeton du site sicopa ne peut pas viser un autre site via l'en-tête.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task TwoFactor_EnrollThenLogin_RequiresSecondFactor()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);
        var client = _factory.CreateClient();
        var (email, password) = await RegisterAsync(client, "Surete");

        // Login simple -> token (2FA pas encore actif).
        var first = await LoginAsync(client, email, password);
        SetBearer(client, first.AccessToken);

        // Enrôlement TOTP.
        var setup = await (await client.PostAsync("/api/auth/2fa/setup", null))
            .Content.ReadFromJsonAsync<TwoFactorSetupDto>(Json);
        var enableResp = await client.PostAsJsonAsync("/api/auth/2fa/enable",
            new EnableTwoFactorRequestDto(Totp(setup!.SharedKey)));
        Assert.Equal(HttpStatusCode.OK, enableResp.StatusCode);

        // Désormais, le login simple exige un second facteur (aucun jeton).
        var noHeaderClient = _factory.CreateClient();
        var step1 = await noHeaderClient.PostAsJsonAsync("/api/auth/login",
            new LoginRequestDto(email, password));
        var step1Body = await step1.Content.ReadFromJsonAsync<TwoFactorRequiredDto>(Json);
        Assert.True(step1Body!.RequiresTwoFactor);

        // Étape 2 avec code TOTP -> jeton délivré.
        var step2 = await noHeaderClient.PostAsJsonAsync("/api/auth/login/2fa",
            new TwoFactorLoginRequestDto(email, password, Totp(setup.SharedKey)));
        Assert.Equal(HttpStatusCode.OK, step2.StatusCode);
        var token = await step2.Content.ReadFromJsonAsync<LoginResponseDto>(Json);
        Assert.False(string.IsNullOrEmpty(token!.AccessToken));

        // Mauvais code -> refusé.
        var bad = await noHeaderClient.PostAsJsonAsync("/api/auth/login/2fa",
            new TwoFactorLoginRequestDto(email, password, "000000"));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
    }

    /// <summary>
    /// Durcissement 2FA : une fois le 2FA activé, /2fa/setup ne doit plus
    /// ré-exposer le secret TOTP (une session détournée pourrait sinon cloner
    /// l'authentificateur de la victime).
    /// </summary>
    [SkippableFact]
    public async Task TwoFactor_Setup_WhenAlreadyEnabled_IsRejected()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);
        var client = _factory.CreateClient();
        var (email, password) = await RegisterAsync(client, "Surete");

        var first = await LoginAsync(client, email, password);
        SetBearer(client, first.AccessToken);

        var setup = await (await client.PostAsync("/api/auth/2fa/setup", null))
            .Content.ReadFromJsonAsync<TwoFactorSetupDto>(Json);
        var enable = await client.PostAsJsonAsync("/api/auth/2fa/enable",
            new EnableTwoFactorRequestDto(Totp(setup!.SharedKey)));
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);

        // Nouvel appel avec la même session : le secret ne doit plus être renvoyé.
        var setupAgain = await client.PostAsync("/api/auth/2fa/setup", null);
        Assert.Equal(HttpStatusCode.BadRequest, setupAgain.StatusCode);
    }

    /// <summary>
    /// Durcissement HTTP (OWASP A05) : les en-têtes de sécurité sont posés sur
    /// toutes les réponses.
    /// </summary>
    [SkippableFact]
    public async Task SecurityHeaders_ArePresentOnResponses()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var resp = await _factory.CreateClient().GetAsync("/health");

        Assert.Equal("nosniff", resp.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", resp.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", resp.Headers.GetValues("Referrer-Policy").Single());
    }

    // ---- Aides ----

    private static CreateVisitRequestDto SampleVisit() => new(
        VisitorName: "Visiteur Test",
        VisitorCompany: "ACME",
        Motif: "Test intégration",
        Mode: "Unique",
        ScheduledAt: DateTimeOffset.UtcNow,
        PlannedDurationMinutes: 60,
        VisitorPhone: null,
        VisitorEmail: null);

    private static void SetBearer(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<LoginResponseDto> CreateAndLoginAsync(HttpClient client, string role)
    {
        var (email, password) = await RegisterAsync(client, role);
        var login = await LoginAsync(client, email, password);
        client.DefaultRequestHeaders.Authorization = null; // repart propre pour l'appelant
        return login;
    }

    private async Task<(string Email, string Password)> RegisterAsync(HttpClient client, string role)
    {
        var admin = await LoginAsync(client, NovAccesApiFactory.AdminEmail, NovAccesApiFactory.AdminPassword);
        var adminClient = _factory.CreateClient();
        SetBearer(adminClient, admin.AccessToken);

        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@sicopa.local";
        const string password = "Test!Passw0rd2026";
        var resp = await adminClient.PostAsJsonAsync("/api/auth/register",
            new RegisterUserRequestDto(email, password, $"{role} Test", role, NovAccesApiFactory.TestSite));
        await EnsureSuccessAsync(resp, "register");
        return (email, password);
    }

    private static async Task<LoginResponseDto> LoginAsync(HttpClient client, string email, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(email, password));
        await EnsureSuccessAsync(resp, $"login {email}");
        return (await resp.Content.ReadFromJsonAsync<LoginResponseDto>(Json))!;
    }

    // Échec explicite (statut + corps) : diagnostic bien plus lisible qu'un
    // EnsureSuccessStatusCode nu lorsqu'un test d'auth casse.
    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, string what)
    {
        if (resp.IsSuccessStatusCode)
            return;
        var body = await resp.Content.ReadAsStringAsync();
        throw new Xunit.Sdk.XunitException($"{what} -> {(int)resp.StatusCode} : {body}");
    }

    // TOTP RFC 6238 (HMAC-SHA1, pas de 30 s, 6 chiffres) pour valider le 2FA.
    private static string Totp(string base32Key)
    {
        var key = Base32Decode(base32Key);
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counterBytes);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24)
                   | ((hash[offset + 1] & 0xff) << 16)
                   | ((hash[offset + 2] & 0xff) << 8)
                   | (hash[offset + 3] & 0xff);
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        input = input.TrimEnd('=').ToUpperInvariant();
        var bits = 0;
        var value = 0;
        var output = new List<byte>();
        foreach (var c in input)
        {
            value = (value << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((value >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}
