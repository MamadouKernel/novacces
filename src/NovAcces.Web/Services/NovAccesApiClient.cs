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

        // Réponse ambiguë : un JWT, { requiresTwoFactor: true }, ou
        // { requiresTwoFactorEnrollment: true }. On lit le corps UNE fois et on
        // distingue par la présence des propriétés.
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(raw);

        if (doc.RootElement.TryGetProperty("requiresTwoFactorEnrollment", out var enroll) && enroll.GetBoolean())
            return LoginOutcome.Enrollment();

        if (doc.RootElement.TryGetProperty("requiresTwoFactor", out var rtf) && rtf.GetBoolean())
            return LoginOutcome.TwoFactor();

        return StoreToken(Deserialize<LoginResponseDto>(raw));
    }

    /// <summary>
    /// Enrôlement 2FA initial d'un compte à privilèges, AVANT toute délivrance
    /// de jeton : l'API vérifie le mot de passe et renvoie la clé TOTP sans
    /// ouvrir de session (c'est tout l'intérêt du parcours « bootstrap »).
    /// </summary>
    public async Task<TwoFactorSetupDto?> BeginTwoFactorEnrollmentAsync(string email, string password)
    {
        var response = await CreateClient(false).PostAsJsonAsync(
            "/api/auth/2fa/bootstrap/setup", new TwoFactorBootstrapRequestDto(email, password));

        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<TwoFactorSetupDto>()
            : null;
    }

    /// <summary>
    /// Active le 2FA après vérification d'un premier code, et renvoie les codes
    /// de récupération — ils ne sont affichés QU'UNE FOIS, l'appelant doit les
    /// présenter à l'utilisateur avant de poursuivre.
    /// </summary>
    public async Task<TwoFactorRecoveryCodesDto?> CompleteTwoFactorEnrollmentAsync(
        string email, string password, string code)
    {
        var response = await CreateClient(false).PostAsJsonAsync(
            "/api/auth/2fa/bootstrap/enable", new TwoFactorBootstrapEnableRequestDto(email, password, code));

        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<TwoFactorRecoveryCodesDto>()
            : null;
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

    /// <summary>
    /// Demande un lien de réinitialisation de mot de passe. La réponse est
    /// toujours "ok" côté serveur (anti-énumération) : on ne peut pas en
    /// déduire si le compte existe.
    /// </summary>
    public async Task<bool> ForgotPasswordAsync(string email)
    {
        var response = await CreateClient(false).PostAsJsonAsync(
            "/api/auth/forgot-password", new ForgotPasswordRequestDto(email));
        return response.IsSuccessStatusCode;
    }

    /// <summary>Finalise la réinitialisation avec le jeton reçu par email.</summary>
    public async Task<(bool Success, string? Error)> ResetPasswordAsync(string email, string token, string newPassword)
    {
        var response = await CreateClient(false).PostAsJsonAsync(
            "/api/auth/reset-password", new ResetPasswordRequestDto(email, token, newPassword));

        return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response));
    }

    /// <summary>Crée une visite. Le site est déduit du jeton côté API (claim).</summary>
    public async Task<CreateVisitResponseDto?> CreateVisitAsync(CreateVisitRequestDto request)
    {
        var response = await CreateClient(true).PostAsJsonAsync("/api/visits", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CreateVisitResponseDto>();
    }

    /// <summary>Création groupée : invite un lot de visiteurs en une opération.</summary>
    public async Task<BulkCreateVisitsResponseDto?> CreateVisitsBulkAsync(BulkCreateVisitsRequestDto request)
    {
        var response = await CreateClient(true).PostAsJsonAsync("/api/visits/bulk", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BulkCreateVisitsResponseDto>();
    }

    /// <summary>Demandes de visite de l'hôte connecté.</summary>
    public async Task<IReadOnlyList<HostVisitDto>> GetMyVisitsAsync()
    {
        var result = await CreateClient(true).GetFromJsonAsync<List<HostVisitDto>>("/api/visits/mine");
        return result ?? new List<HostVisitDto>();
    }

    /// <summary>Chronologie (statuts) d'une demande de visite.</summary>
    public async Task<VisitHistoryDto?> GetVisitHistoryAsync(Guid visitId)
        => await CreateClient(true).GetFromJsonAsync<VisitHistoryDto>($"/api/visits/{visitId}/history");

    /// <summary>Visiteurs déjà connus du site (autocomplétion + pré-remplissage).</summary>
    public async Task<IReadOnlyList<KnownVisitorDto>> GetKnownVisitorsAsync()
    {
        var result = await CreateClient(true).GetFromJsonAsync<List<KnownVisitorDto>>("/api/visits/known-visitors");
        return result ?? new List<KnownVisitorDto>();
    }

    /// <summary>Révoque une demande (REQ-F-09). Vrai si la révocation a réussi.</summary>
    public async Task<bool> RevokeVisitAsync(Guid visitId)
    {
        var response = await CreateClient(true).PostAsync($"/api/visits/{visitId}/revoke", null);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Corrige les coordonnées d'une demande avant l'arrivée du visiteur (erreur de saisie).</summary>
    public async Task<(bool Success, string? Error)> UpdateVisitAsync(Guid visitId, UpdateVisitRequestDto request)
    {
        var response = await CreateClient(true).PutAsJsonAsync($"/api/visits/{visitId}", request);
        return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response));
    }

    /// <summary>
    /// Réémet le QR d'une demande existante (visiteur ayant perdu son
    /// message). Le message d'erreur est distingué du cas générique : un 409
    /// (QR révoqué/expiré/déjà utilisé) mérite d'être expliqué à l'hôte,
    /// pas noyé dans « service indisponible ».
    /// </summary>
    public async Task<ReissueQrOutcome> ReissueVisitQrAsync(Guid visitId)
    {
        var response = await CreateClient(true).GetAsync($"/api/visits/{visitId}/qr");

        if (response.IsSuccessStatusCode)
        {
            var data = await response.Content.ReadFromJsonAsync<CreateVisitResponseDto>();
            return data is null
                ? ReissueQrOutcome.Failed("Réponse inattendue du service.")
                : ReissueQrOutcome.Ok(data);
        }

        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            var raw = await response.Content.ReadAsStringAsync();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("error", out var msg))
                    return ReissueQrOutcome.Failed(msg.GetString() ?? "QR indisponible.");
            }
            catch (System.Text.Json.JsonException) { /* réponse non structurée : repli générique */ }
        }

        return ReissueQrOutcome.Failed("Impossible de récupérer ce QR pour le moment.");
    }

    /// <summary>Journal des derniers scans du site, avec recherche optionnelle.</summary>
    public async Task<IReadOnlyList<ScanJournalEntryDto>> GetJournalAsync(int limit = 50, string? query = null)
    {
        var url = $"/api/dashboard/journal?limit={limit}";
        if (!string.IsNullOrWhiteSpace(query))
            url += $"&q={Uri.EscapeDataString(query.Trim())}";
        var result = await CreateClient(true).GetFromJsonAsync<List<ScanJournalEntryDto>>(url);
        return result ?? new List<ScanJournalEntryDto>();
    }

    /// <summary>Visiteurs actuellement présents sur le site.</summary>
    public async Task<IReadOnlyList<OnSiteVisitorDto>> GetOnSiteAsync()
    {
        var result = await CreateClient(true)
            .GetFromJsonAsync<List<OnSiteVisitorDto>>("/api/dashboard/on-site");
        return result ?? new List<OnSiteVisitorDto>();
    }

    /// <summary>
    /// Sortie manuelle depuis le dashboard sûreté (§7) : le visiteur a quitté
    /// le site sans scanner (téléphone déchargé, autre accès, oubli). Sans
    /// cette action, il resterait « présent » et en dépassement indéfiniment.
    /// </summary>
    public async Task<bool> ManualCheckOutAsync(Guid visitId)
    {
        var response = await CreateClient(true).PostAsync($"/api/dashboard/on-site/{visitId}/check-out", null);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Synthèse du jour (dashboard sûreté).</summary>
    public async Task<DashboardSummaryDto?> GetSummaryAsync()
        => await CreateClient(true).GetFromJsonAsync<DashboardSummaryDto>("/api/dashboard/summary");

    /// <summary>Contenu CSV du journal (pour export/téléchargement).</summary>
    public async Task<string> GetJournalCsvAsync()
        => await CreateClient(true).GetStringAsync("/api/dashboard/journal.csv");

    // ---- Liste d'exclusion (Sûreté/Admin) ----

    public async Task<IReadOnlyList<ExclusionDto>> GetExclusionsAsync()
    {
        var result = await CreateClient(true).GetFromJsonAsync<List<ExclusionDto>>("/api/exclusions");
        return result ?? new List<ExclusionDto>();
    }

    public async Task<bool> AddExclusionAsync(string displayName, string reason)
    {
        var response = await CreateClient(true)
            .PostAsJsonAsync("/api/exclusions", new AddExclusionRequestDto(displayName, reason));
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> RemoveExclusionAsync(Guid id)
    {
        var response = await CreateClient(true).PostAsync($"/api/exclusions/{id}/remove", null);
        return response.IsSuccessStatusCode;
    }

    // ---- Administration ----

    public async Task<IReadOnlyList<AdminUserDto>> GetUsersAsync()
    {
        var result = await CreateClient(true).GetFromJsonAsync<List<AdminUserDto>>("/api/admin/users");
        return result ?? new List<AdminUserDto>();
    }

    public async Task<IReadOnlyList<AdminSiteOverviewDto>> GetSiteOverviewAsync()
    {
        var result = await CreateClient(true).GetFromJsonAsync<List<AdminSiteOverviewDto>>("/api/admin/overview");
        return result ?? new List<AdminSiteOverviewDto>();
    }

    /// <summary>Tendance d'activité multi-sites sur les N derniers jours (dashboard Admin).</summary>
    public async Task<AdminTrendsDto?> GetTrendsAsync(int days)
        => await CreateClient(true).GetFromJsonAsync<AdminTrendsDto>($"/api/admin/trends?days={days}");

    public async Task<(bool Success, string? Error)> RegisterUserAsync(RegisterUserRequestDto request)
    {
        var response = await CreateClient(true).PostAsJsonAsync("/api/auth/register", request);
        return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response));
    }

    /// <summary>Désactivation logique d'un compte (motif obligatoire, 5 à 500 caractères).</summary>
    public async Task<(bool Success, string? Error)> DeactivateUserAsync(Guid id, string reason)
    {
        var response = await CreateClient(true)
            .PostAsJsonAsync($"/api/admin/users/{id}/deactivate", new DeactivateUserRequestDto(reason));
        return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response));
    }

    /// <summary>Réactive un compte désactivé (réservé SuperAdmin côté API).</summary>
    public async Task<(bool Success, string? Error)> ReactivateUserAsync(Guid id)
    {
        var response = await CreateClient(true).PostAsync($"/api/admin/users/{id}/reactivate", null);
        return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response));
    }

    /// <summary>Modifie l'email, le nom, le rôle et le site d'un compte.</summary>
    public async Task<(bool Success, string? Error)> UpdateUserAsync(Guid id, string email, string displayName, string role, string? siteId)
    {
        var response = await CreateClient(true)
            .PutAsJsonAsync($"/api/admin/users/{id}", new UpdateUserRequestDto(email, displayName, role, siteId));
        return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response));
    }

    /// <summary>Réinitialise le mot de passe d'un compte.</summary>
    public async Task<(bool Success, string? Error)> ResetUserPasswordAsync(Guid id, string newPassword)
    {
        var response = await CreateClient(true)
            .PostAsJsonAsync($"/api/admin/users/{id}/reset-password", new AdminResetPasswordRequestDto(newPassword));
        return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response));
    }

    /// <summary>État de la politique de rétention en vigueur.</summary>
    public async Task<RetentionStatusDto?> GetRetentionStatusAsync()
        => await CreateClient(true).GetFromJsonAsync<RetentionStatusDto>("/api/admin/retention");

    /// <summary>Déclenche une passe immédiate de rétention (purge + anonymisation).</summary>
    public async Task<RetentionRunResultDto?> RunRetentionAsync()
    {
        var response = await CreateClient(true).PostAsync("/api/admin/retention/run", null);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<RetentionRunResultDto>()
            : null;
    }

    /// <summary>
    /// Journal d'audit des actions privilégiées d'UN site (§8.5) — endpoint
    /// tenant-résolu par en-tête, l'Admin global n'a pas de site dans son jeton.
    /// </summary>
    public async Task<IReadOnlyList<AdminAuditDto>> GetAdminAuditAsync(string siteId, int limit = 100)
    {
        var client = CreateClient(true);
        client.DefaultRequestHeaders.Remove("X-Site-Id");
        client.DefaultRequestHeaders.Add("X-Site-Id", siteId);
        var result = await client.GetFromJsonAsync<List<AdminAuditDto>>($"/api/audit?limit={limit}");
        return result ?? new List<AdminAuditDto>();
    }

    /// <summary>Journal technique global de toutes les requêtes API (SuperAdmin uniquement).</summary>
    public async Task<IReadOnlyList<ApplicationAuditDto>> GetApplicationAuditAsync(int limit = 200)
    {
        var result = await CreateClient(true).GetFromJsonAsync<List<ApplicationAuditDto>>($"/api/audit/application?limit={limit}");
        return result ?? new List<ApplicationAuditDto>();
    }

    /// <summary>Contenu CSV du journal technique global (SuperAdmin uniquement).</summary>
    public async Task<string> GetApplicationAuditCsvAsync()
        => await CreateClient(true).GetStringAsync("/api/audit/application.csv");

    /// <summary>Modifie le nom affiché de l'utilisateur connecté.</summary>
    public async Task<(bool Success, string? Error)> UpdateDisplayNameAsync(string displayName)
    {
        var response = await CreateClient(true).PostAsJsonAsync(
            "/api/auth/me/display-name", new UpdateDisplayNameRequestDto(displayName));
        return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response));
    }

    /// <summary>Change le mot de passe de l'utilisateur connecté.</summary>
    public async Task<(bool Success, string? Error)> ChangePasswordAsync(string current, string @new)
    {
        var response = await CreateClient(true).PostAsJsonAsync(
            "/api/auth/me/password", new ChangePasswordRequestDto(current, @new));
        return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response));
    }

    /// <summary>Change l'email (identifiant de connexion) de l'utilisateur connecté ; mot de passe requis.</summary>
    public async Task<(bool Success, string? Error)> ChangeEmailAsync(string currentPassword, string newEmail)
    {
        var response = await CreateClient(true).PostAsJsonAsync(
            "/api/auth/me/email", new ChangeEmailRequestDto(currentPassword, newEmail));
        return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response));
    }

    /// <summary>État actuel du 2FA pour l'utilisateur connecté (profil).</summary>
    public async Task<bool> GetTwoFactorStatusAsync()
    {
        var response = await CreateClient(true).GetAsync("/api/auth/me/2fa-status");
        if (!response.IsSuccessStatusCode) return false;
        var status = await response.Content.ReadFromJsonAsync<TwoFactorStatusDto>();
        return status?.Enabled ?? false;
    }

    /// <summary>Ré-enrôlement 2FA depuis le profil (utilisateur déjà authentifié).</summary>
    public async Task<TwoFactorSetupDto?> SetupTwoFactorAsync()
    {
        var response = await CreateClient(true).PostAsync("/api/auth/2fa/setup", null);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<TwoFactorSetupDto>()
            : null;
    }

    /// <summary>Active le 2FA depuis le profil ; renvoie les codes de récupération.</summary>
    public async Task<(TwoFactorRecoveryCodesDto? Codes, string? Error)> EnableTwoFactorAsync(string code)
    {
        var response = await CreateClient(true).PostAsJsonAsync("/api/auth/2fa/enable", new EnableTwoFactorRequestDto(code));
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync<TwoFactorRecoveryCodesDto>(), null)
            : (null, await ReadErrorAsync(response));
    }

    /// <summary>Désactive le 2FA depuis le profil (mot de passe requis).</summary>
    public async Task<(bool Success, string? Error)> DisableTwoFactorAsync(string password)
    {
        var response = await CreateClient(true).PostAsJsonAsync("/api/auth/2fa/disable", new DisableTwoFactorRequestDto(password));
        return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> ProvisionSiteAsync(string siteId)
    {
        var response = await CreateClient(true).PostAsJsonAsync("/api/admin/sites", new ProvisionSiteRequestDto(siteId));
        return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response));
    }

    /// <summary>Désactive un site (contrat non reconduit) : coupe l'accès sans supprimer les données.</summary>
    public async Task<(bool Success, string? Error)> DeactivateSiteAsync(string siteId, string reason)
    {
        var response = await CreateClient(true)
            .PostAsJsonAsync($"/api/admin/sites/{siteId}/deactivate", new DeactivateSiteRequestDto(reason));
        return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response));
    }

    /// <summary>Réactive un site désactivé (SuperAdmin uniquement).</summary>
    public async Task<(bool Success, string? Error)> ReactivateSiteAsync(string siteId)
    {
        var response = await CreateClient(true).PostAsync($"/api/admin/sites/{siteId}/reactivate", null);
        return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response));
    }

    /// <summary>Liste les agents (matricule + nom) d'un site.</summary>
    public async Task<IReadOnlyList<AgentSummaryDto>> GetAgentsAsync(string siteId)
    {
        var result = await CreateClient(true).GetFromJsonAsync<List<AgentSummaryDto>>($"/api/admin/agents/{siteId}");
        return result ?? new List<AgentSummaryDto>();
    }

    /// <summary>Crée un agent (matricule + PIN) pour la prise de poste sur un site.</summary>
    public async Task<(bool Success, string? Error)> CreateAgentAsync(string siteId, string matricule, string displayName, string pin)
    {
        var response = await CreateClient(true).PostAsJsonAsync("/api/admin/agents",
            new CreateAgentRequestDto(siteId, matricule, displayName, pin));
        return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response));
    }

    /// <summary>Désactive un agent sur un site (départ, réaffectation vers un autre site).</summary>
    public async Task<(bool Success, string? Error)> DeactivateAgentAsync(string siteId, string matricule)
    {
        var response = await CreateClient(true).PostAsync(
            $"/api/admin/agents/{Uri.EscapeDataString(siteId)}/{Uri.EscapeDataString(matricule)}/deactivate", null);
        return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response));
    }

    /// <summary>Liste les terminaux enrôlés (jamais leur clé).</summary>
    public async Task<IReadOnlyList<TerminalSummaryDto>> GetTerminalsAsync()
    {
        var result = await CreateClient(true).GetFromJsonAsync<List<TerminalSummaryDto>>("/api/admin/terminals");
        return result ?? new List<TerminalSummaryDto>();
    }

    /// <summary>Provisionne un terminal ; le Web demande ensuite un ticket QR temporaire.</summary>
    public async Task<(bool Success, CreateTerminalResponseDto? Result, string? Error)> CreateTerminalAsync(
        string label, IReadOnlyList<string> siteIds)
    {
        var response = await CreateClient(true).PostAsJsonAsync("/api/admin/terminals",
            new CreateTerminalRequestDto(label, siteIds));
        if (!response.IsSuccessStatusCode)
            return (false, null, await ReadErrorAsync(response));

        return (true, await response.Content.ReadFromJsonAsync<CreateTerminalResponseDto>(), null);
    }

    /// <summary>Génère un ticket QR d'enrôlement temporaire pour un terminal.</summary>
    public async Task<(bool Success, EnrollmentTicketResponseDto? Result, string? Error)> CreateEnrollmentTicketAsync(Guid id)
    {
        var response = await CreateClient(true).PostAsync($"/api/admin/terminals/{id}/enrollment-ticket", null);
        if (!response.IsSuccessStatusCode)
            return (false, null, await ReadErrorAsync(response));
        return (true, await response.Content.ReadFromJsonAsync<EnrollmentTicketResponseDto>(), null);
    }

    public async Task<(bool Success, string? Error)> RevokeTerminalAsync(Guid id)
    {
        var response = await CreateClient(true).PostAsync($"/api/admin/terminals/{id}/revoke", null);
        return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response));
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var raw = await response.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var message = doc.RootElement.TryGetProperty("error", out var err) ? err.GetString() : null;

            // Certains endpoints (ex. /auth/register) joignent les motifs précis
            // (règles Identity : mot de passe, email déjà pris…) dans "details" —
            // sans quoi l'utilisateur ne voit qu'un refus générique inexploitable.
            if (doc.RootElement.TryGetProperty("details", out var detailsEl)
                && detailsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var details = detailsEl.EnumerateArray()
                    .Select(d => d.GetString())
                    .Where(d => !string.IsNullOrWhiteSpace(d));
                var joined = string.Join(" ", details);
                if (!string.IsNullOrWhiteSpace(joined))
                    return string.IsNullOrWhiteSpace(message) ? joined : $"{message} {joined}";
            }

            return message ?? "Échec.";
        }
        catch { /* corps non JSON */ }
        return $"Échec ({(int)response.StatusCode}).";
    }

    private static readonly System.Text.Json.JsonSerializerOptions WebJson =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    private static T? Deserialize<T>(string raw) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(raw, WebJson);

    private LoginOutcome StoreToken(LoginResponseDto? login)
    {
        if (login is null || string.IsNullOrEmpty(login.AccessToken))
            return LoginOutcome.Failed("Réponse d'authentification inattendue.");

        _auth.SignIn(login.AccessToken, login.DisplayName, login.Email, login.Roles, login.SiteId);
        return LoginOutcome.Connected();
    }
}

/// <summary>
/// Issue d'une tentative de connexion. Trois cas, pas deux :
///  - <see cref="Success"/> : jeton obtenu ;
///  - <see cref="RequiresTwoFactor"/> : compte DÉJÀ enrôlé, il faut un code ;
///  - <see cref="RequiresEnrollment"/> : compte à privilèges JAMAIS enrôlé, il
///    doit d'abord configurer son authentificateur. C'est l'état de tout compte
///    Admin/Sûreté fraîchement créé — y compris l'Admin d'amorçage en
///    production. Ne pas traiter ce cas rendait le portail inaccessible.
/// </summary>
public sealed record ReissueQrOutcome(bool Success, CreateVisitResponseDto? Data, string? Error)
{
    public static ReissueQrOutcome Ok(CreateVisitResponseDto data) => new(true, data, null);
    public static ReissueQrOutcome Failed(string error) => new(false, null, error);
}

public sealed record LoginOutcome(bool Success, bool RequiresTwoFactor, bool RequiresEnrollment, string? Error)
{
    public static LoginOutcome Connected() => new(true, false, false, null);
    public static LoginOutcome TwoFactor() => new(false, true, false, null);
    public static LoginOutcome Enrollment() => new(false, false, true, null);
    public static LoginOutcome Failed(string error) => new(false, false, false, error);
}
