using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using NovAcces.Shared.Dtos;

namespace NovAcces.Web.Services;

/// <summary>
/// Client typé de l'API NovAcces. Ne réimplémente aucune règle métier ni de
/// sécurité : il consomme l'API (qui reste l'unique point d'application de
/// l'authentification, du RBAC et du cloisonnement). Le JWT du circuit courant
/// est joint automatiquement.
/// </summary>
public sealed class NovAccesApiClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly AuthState _auth;

    public NovAccesApiClient(IHttpClientFactory httpFactory, AuthState auth)
    {
        _httpFactory = httpFactory;
        _auth = auth;
    }

    private HttpClient CreateClient(bool authenticated)
    {
        var client = _httpFactory.CreateClient("Api");
        if (authenticated && _auth.AccessToken is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _auth.AccessToken);
        return client;
    }

    /// <summary>
    /// Étape 1 de connexion. Résultat : soit connecté (jeton posé dans AuthState),
    /// soit un second facteur requis, soit un échec.
    /// </summary>
    public async Task<LoginOutcome> LoginAsync(string email, string password)
    {
        var response = await CreateClient(false).PostAsJsonAsync("/api/auth/login", new LoginRequestDto(email, password));

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return LoginOutcome.Failed("Identifiants invalides.");
        if (!response.IsSuccessStatusCode)
            return LoginOutcome.Failed("Service indisponible, réessayez.");

        // Réponse ambiguë : soit un JWT, soit { requiresTwoFactor: true }. On lit
        // le corps UNE fois et on distingue par la présence des propriétés.
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("requiresTwoFactor", out var rtf) && rtf.GetBoolean())
            return LoginOutcome.TwoFactor();

        return StoreToken(Deserialize<LoginResponseDto>(raw));
    }

    /// <summary>Étape 2 : validation du second facteur (TOTP ou code de récupération).</summary>
    public async Task<LoginOutcome> LoginTwoFactorAsync(string email, string password, string code)
    {
        var response = await CreateClient(false).PostAsJsonAsync(
            "/api/auth/login/2fa", new TwoFactorLoginRequestDto(email, password, code));

        if (!response.IsSuccessStatusCode)
            return LoginOutcome.Failed("Code invalide.");

        var login = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        return StoreToken(login);
    }

    /// <summary>Crée une visite. Le site est déduit du jeton côté API (claim).</summary>
    public async Task<CreateVisitResponseDto?> CreateVisitAsync(CreateVisitRequestDto request)
    {
        var response = await CreateClient(true).PostAsJsonAsync("/api/visits", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CreateVisitResponseDto>();
    }

    /// <summary>Journal des derniers scans du site (dashboard sûreté).</summary>
    public async Task<IReadOnlyList<ScanJournalEntryDto>> GetJournalAsync(int limit = 50)
    {
        var result = await CreateClient(true)
            .GetFromJsonAsync<List<ScanJournalEntryDto>>($"/api/dashboard/journal?limit={limit}");
        return result ?? new List<ScanJournalEntryDto>();
    }

    /// <summary>Visiteurs actuellement présents sur le site.</summary>
    public async Task<IReadOnlyList<OnSiteVisitorDto>> GetOnSiteAsync()
    {
        var result = await CreateClient(true)
            .GetFromJsonAsync<List<OnSiteVisitorDto>>("/api/dashboard/on-site");
        return result ?? new List<OnSiteVisitorDto>();
    }

    private static readonly System.Text.Json.JsonSerializerOptions WebJson =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    private static T? Deserialize<T>(string raw) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(raw, WebJson);

    private LoginOutcome StoreToken(LoginResponseDto? login)
    {
        if (login is null || string.IsNullOrEmpty(login.AccessToken))
            return LoginOutcome.Failed("Réponse d'authentification inattendue.");

        _auth.SignIn(login.AccessToken, login.DisplayName, login.Roles, login.SiteId);
        return LoginOutcome.Connected();
    }
}

public sealed record LoginOutcome(bool Success, bool RequiresTwoFactor, string? Error)
{
    public static LoginOutcome Connected() => new(true, false, null);
    public static LoginOutcome TwoFactor() => new(false, true, null);
    public static LoginOutcome Failed(string error) => new(false, false, error);
}
