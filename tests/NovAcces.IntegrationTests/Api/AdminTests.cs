using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NovAcces.Shared.Dtos;
using Xunit;

namespace NovAcces.IntegrationTests.Api;

/// <summary>
/// Console d'administration : liste des comptes et provisionnement de site,
/// réservés au rôle Admin.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AdminTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NovAccesApiFactory _factory;

    public AdminTests(NovAccesApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task Users_ListedByAdmin_ForbiddenForHote()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var admin = await AdminClientAsync();
        var users = await admin.GetFromJsonAsync<List<AdminUserDto>>("/api/admin/users", Json);
        Assert.Contains(users!, u => u.Email == NovAccesApiFactory.AdminEmail);

        var hote = await NewUserClientAsync("Hote");
        var forbidden = await hote.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var anon = await _factory.CreateClient().GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
    }

    [SkippableFact]
    public async Task ProvisionSite_ByAdmin_CreatesSchema()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var siteId = $"adm{Guid.NewGuid():N}".Substring(0, 20);
        var admin = await AdminClientAsync();
        try
        {
            var resp = await admin.PostAsJsonAsync("/api/admin/sites", new ProvisionSiteRequestDto(siteId));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.True(await SchemaHasTableAsync($"site_{siteId}", "visits"));
        }
        finally
        {
            await DropSchemaAsync($"site_{siteId}");
        }
    }

    [SkippableFact]
    public async Task Overview_ListsSites_ForbiddenForNonAdmin()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var admin = await AdminClientAsync();
        var overview = await admin.GetFromJsonAsync<List<AdminSiteOverviewDto>>("/api/admin/overview", Json);
        Assert.Contains(overview!, s => s.SiteId == NovAccesApiFactory.TestSite);

        var surete = await NewUserClientAsync("Surete");
        var forbidden = await surete.GetAsync("/api/admin/overview");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [SkippableFact]
    public async Task ProvisionSite_ForbiddenForNonAdmin()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var surete = await NewUserClientAsync("Surete");
        var resp = await surete.PostAsJsonAsync("/api/admin/sites", new ProvisionSiteRequestDto("whatever"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [SkippableFact]
    public async Task CreateTerminal_ByAdmin_DoesNotExposeApiKey()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var admin = await AdminClientAsync();
        var label = $"Terminal-{Guid.NewGuid():N}";

        var create = await admin.PostAsJsonAsync("/api/admin/terminals",
            new CreateTerminalRequestDto(label, new[] { NovAccesApiFactory.TestSite }));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<CreateTerminalResponseDto>(Json);
        Assert.NotNull(created);

        var listed = await admin.GetFromJsonAsync<List<TerminalSummaryDto>>("/api/admin/terminals", Json);
        var summary = listed!.Single(t => t.Id == created!.Id);
        Assert.Equal(label, summary.Label);
        Assert.True(summary.IsActive);
        Assert.False(summary.IsEnrolled);
    }

    [SkippableFact]
    public async Task TerminalEnrollmentTicket_IsOneTimeAndActivatesDevice()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var admin = await AdminClientAsync();
        var create = await admin.PostAsJsonAsync("/api/admin/terminals",
            new CreateTerminalRequestDto($"QR-{Guid.NewGuid():N}", new[] { NovAccesApiFactory.TestSite }));
        var created = await create.Content.ReadFromJsonAsync<CreateTerminalResponseDto>(Json);
        Assert.NotNull(created);

        var ticketResponse = await admin.PostAsync($"/api/admin/terminals/{created!.Id}/enrollment-ticket", null);
        Assert.Equal(HttpStatusCode.OK, ticketResponse.StatusCode);
        var ticket = await ticketResponse.Content.ReadFromJsonAsync<EnrollmentTicketResponseDto>(Json);
        Assert.NotNull(ticket);
        Assert.Contains("novacces://enroll", ticket!.QrPayload);
        Assert.True(ticket.ExpiresAt > DateTimeOffset.UtcNow);

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var deviceId = Guid.NewGuid().ToString("D");
        var activation = await _factory.CreateClient().PostAsJsonAsync(
            "/api/device-enrollments/activate",
            EnrollmentRequest(ecdsa, TicketFromQr(ticket.QrPayload), deviceId));
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
        var activated = await activation.Content.ReadFromJsonAsync<DeviceEnrollmentActivationDto>(Json);
        Assert.NotNull(activated);
        Assert.False(string.IsNullOrWhiteSpace(activated!.ApiKey));

        var terminal = _factory.CreateClient();
        terminal.DefaultRequestHeaders.Add("X-Api-Key", activated.ApiKey);
        Assert.Equal(HttpStatusCode.OK, (await terminal.GetAsync("/api/agent/sites")).StatusCode);

        // Le même ticket ne peut plus activer un deuxième téléphone.
        var second = await _factory.CreateClient().PostAsJsonAsync(
            "/api/device-enrollments/activate",
            EnrollmentRequest(ecdsa, TicketFromQr(ticket.QrPayload), Guid.NewGuid().ToString("D")));
        Assert.Equal(HttpStatusCode.Gone, second.StatusCode);

        // Un nouveau ticket réenrôle le terminal et invalide la clé précédente.
        var replacementTicketResponse = await admin.PostAsync($"/api/admin/terminals/{created!.Id}/enrollment-ticket", null);
        var replacementTicket = await replacementTicketResponse.Content.ReadFromJsonAsync<EnrollmentTicketResponseDto>(Json);
        var replacement = await _factory.CreateClient().PostAsJsonAsync(
            "/api/device-enrollments/activate",
            EnrollmentRequest(ecdsa, TicketFromQr(replacementTicket!.QrPayload), Guid.NewGuid().ToString("D")));
        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);

        var oldKey = _factory.CreateClient();
        oldKey.DefaultRequestHeaders.Add("X-Api-Key", activated.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldKey.GetAsync("/api/agent/sites")).StatusCode);
    }
    [SkippableFact]
    public async Task DeviceActivation_WithoutProofOfPossession_IsRejected()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var admin = await AdminClientAsync();
        var create = await admin.PostAsJsonAsync("/api/admin/terminals",
            new CreateTerminalRequestDto($"PoP-{Guid.NewGuid():N}", new[] { NovAccesApiFactory.TestSite }));
        var created = await create.Content.ReadFromJsonAsync<CreateTerminalResponseDto>(Json);
        var ticketResponse = await admin.PostAsync($"/api/admin/terminals/{created!.Id}/enrollment-ticket", null);
        var ticket = await ticketResponse.Content.ReadFromJsonAsync<EnrollmentTicketResponseDto>(Json);
        var rawTicket = TicketFromQr(ticket!.QrPayload);

        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var deviceId = Guid.NewGuid().ToString("D");

        // 1. Aucune preuve : la clé publique déclarée n'atteste de rien.
        var noProof = await _factory.CreateClient().PostAsJsonAsync(
            "/api/device-enrollments/activate",
            new DeviceEnrollmentRequestDto(rawTicket, deviceId, deviceKey.ExportSubjectPublicKeyInfoPem()));
        Assert.Equal(HttpStatusCode.BadRequest, noProof.StatusCode);

        // 2. Preuve signée par une AUTRE clé que celle déclarée : c'est le
        //    scénario du ticket intercepté par un appareil tiers.
        using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var wrongProof = await _factory.CreateClient().PostAsJsonAsync(
            "/api/device-enrollments/activate",
            new DeviceEnrollmentRequestDto(rawTicket, deviceId, deviceKey.ExportSubjectPublicKeyInfoPem(),
                SignProof(attackerKey, rawTicket, deviceId)));
        Assert.Equal(HttpStatusCode.BadRequest, wrongProof.StatusCode);

        // 3. Le ticket n'a PAS été consommé par ces tentatives : le device
        //    légitime peut toujours s'enrôler.
        var legitimate = await _factory.CreateClient().PostAsJsonAsync(
            "/api/device-enrollments/activate",
            EnrollmentRequest(deviceKey, rawTicket, deviceId));
        Assert.Equal(HttpStatusCode.OK, legitimate.StatusCode);
    }

    [SkippableFact]
    public async Task CreateTerminal_ForbiddenForNonAdmin()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var surete = await NewUserClientAsync("Surete");
        var resp = await surete.PostAsJsonAsync("/api/admin/terminals",
            new CreateTerminalRequestDto("Intrus", new[] { NovAccesApiFactory.TestSite }));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [SkippableFact]
    public async Task RevokeTerminal_DisablesFutureAuthentication()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var admin = await AdminClientAsync();
        var create = await admin.PostAsJsonAsync("/api/admin/terminals",
            new CreateTerminalRequestDto($"ARévoquer-{Guid.NewGuid():N}", new[] { NovAccesApiFactory.TestSite }));
        var created = await create.Content.ReadFromJsonAsync<CreateTerminalResponseDto>(Json);
        Assert.NotNull(created);
        var ticketResponse = await admin.PostAsync($"/api/admin/terminals/{created!.Id}/enrollment-ticket", null);
        var ticket = await ticketResponse.Content.ReadFromJsonAsync<EnrollmentTicketResponseDto>(Json);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var activationResponse = await _factory.CreateClient().PostAsJsonAsync(
            "/api/device-enrollments/activate",
            EnrollmentRequest(ecdsa, TicketFromQr(ticket!.QrPayload), Guid.NewGuid().ToString("D")));
        var activated = await activationResponse.Content.ReadFromJsonAsync<DeviceEnrollmentActivationDto>(Json);
        var terminalClient = _factory.CreateClient();
        terminalClient.DefaultRequestHeaders.Add("X-Api-Key", activated!.ApiKey);
        Assert.Equal(HttpStatusCode.OK, (await terminalClient.GetAsync("/api/agent/sites")).StatusCode);

        var revoke = await admin.PostAsync($"/api/admin/terminals/{created.Id}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        // Même clé, présentée après révocation : refusée.
        var afterRevoke = await terminalClient.GetAsync("/api/agent/sites");
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);

        var listed = await admin.GetFromJsonAsync<List<TerminalSummaryDto>>("/api/admin/terminals", Json);
        Assert.False(listed!.Single(t => t.Id == created.Id).IsActive);
    }

    [SkippableFact]
    public async Task Deactivation_RespectsRoleHierarchy_AndProtectsLastSuperAdmin()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var superAdmin = await AdminClientAsync();
        var email = $"admin-target-{Guid.NewGuid():N}@sicopa.local";
        const string password = "Test!Passw0rd2026";
        var register = await superAdmin.PostAsJsonAsync("/api/auth/register",
            new RegisterUserRequestDto(email, password, "Admin cible", "Admin", null));
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);

        var plainClient = _factory.CreateClient();
        var login = await plainClient.PostAsJsonAsync(
            "/api/auth/login", new LoginRequestDto(email, password));
        login.EnsureSuccessStatusCode();
        var loginDto = await login.Content.ReadFromJsonAsync<LoginResponseDto>(Json);
        plainClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginDto!.AccessToken);

        var users = await plainClient.GetFromJsonAsync<List<AdminUserDto>>("/api/admin/users", Json);
        var own = Assert.Single(users!, u => u.Email == email);
        var forbidden = await plainClient.PostAsJsonAsync(
            $"/api/admin/users/{own.Id}/deactivate",
            new DeactivateUserRequestDto("Tentative non autorisée"));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var all = await superAdmin.GetFromJsonAsync<List<AdminUserDto>>("/api/admin/users", Json);
        var lastSuperAdmin = Assert.Single(all!, u =>
            u.Email == NovAccesApiFactory.AdminEmail && u.Roles.Contains("SuperAdmin"));
        var conflict = await superAdmin.PostAsJsonAsync(
            $"/api/admin/users/{lastSuperAdmin.Id}/deactivate",
            new DeactivateUserRequestDto("Test garde dernier SuperAdmin"));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    /// <summary>
    /// Un Admin (pas seulement SuperAdmin) peut désormais réactiver un compte
    /// qu'il gère (Hôte, Sûreté, autre Admin) — même hiérarchie que la
    /// désactivation/édition. Un SuperAdmin reste hors de portée d'un Admin.
    /// </summary>
    [SkippableFact]
    public async Task Reactivation_AllowedForAdmin_OnManagedAccounts_NotOnSuperAdmin()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var superAdmin = await AdminClientAsync();
        const string password = "Test!Passw0rd2026";

        // Un Admin réactive un Hôte qu'il a lui-même désactivé.
        var admin = await NewUserClientAsync("Admin");
        var hoteEmail = $"hote-reactiv-{Guid.NewGuid():N}@sicopa.local";
        var registerHote = await admin.PostAsJsonAsync("/api/auth/register",
            new RegisterUserRequestDto(hoteEmail, password, "Hôte À Réactiver", "Hote", NovAccesApiFactory.TestSite));
        Assert.Equal(HttpStatusCode.OK, registerHote.StatusCode);
        var hoteId = Assert.Single(
            (await admin.GetFromJsonAsync<List<AdminUserDto>>("/api/admin/users", Json))!,
            u => u.Email == hoteEmail).Id;

        var deactivate = await admin.PostAsJsonAsync(
            $"/api/admin/users/{hoteId}/deactivate", new DeactivateUserRequestDto("Test réactivation par Admin"));
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var reactivate = await admin.PostAsync($"/api/admin/users/{hoteId}/reactivate", null);
        Assert.Equal(HttpStatusCode.OK, reactivate.StatusCode);

        var afterReactivate = Assert.Single(
            (await superAdmin.GetFromJsonAsync<List<AdminUserDto>>("/api/admin/users", Json))!, u => u.Id == hoteId);
        Assert.False(afterReactivate.IsDeactivated);

        // Ce même Admin ne peut pas réactiver un SuperAdmin (invisible/hors
        // hiérarchie) : on ne peut même pas retrouver son Id via cet Admin,
        // mais l'appel direct avec l'Id réel du SuperAdmin doit rester refusé.
        var allAsSuperAdmin = await superAdmin.GetFromJsonAsync<List<AdminUserDto>>("/api/admin/users", Json);
        var superAdminId = Assert.Single(allAsSuperAdmin!, u =>
            u.Email == NovAccesApiFactory.AdminEmail && u.Roles.Contains("SuperAdmin")).Id;
        var forbidden = await admin.PostAsync($"/api/admin/users/{superAdminId}/reactivate", null);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    /// <summary>
    /// Un agent désactivé (départ d'un site) peut être réactivé s'il revient —
    /// il retrouve son matricule d'origine plutôt qu'un nouveau.
    /// </summary>
    [SkippableFact]
    public async Task ReactivateAgent_RestoresOriginalMatricule()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var admin = await AdminClientAsync();
        var matricule = $"AG-{Guid.NewGuid():N}".Substring(0, 12);

        var create = await admin.PostAsJsonAsync("/api/admin/agents",
            new CreateAgentRequestDto(NovAccesApiFactory.TestSite, matricule, "Agent Test Réactivation", "1234"));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var deactivate = await admin.PostAsync(
            $"/api/admin/agents/{NovAccesApiFactory.TestSite}/{matricule}/deactivate", null);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var listedAfterDeactivate = await admin.GetFromJsonAsync<List<AgentSummaryDto>>(
            $"/api/admin/agents/{NovAccesApiFactory.TestSite}", Json);
        Assert.False(listedAfterDeactivate!.Single(a => a.Matricule == matricule).IsActive);

        var reactivate = await admin.PostAsync(
            $"/api/admin/agents/{NovAccesApiFactory.TestSite}/{matricule}/reactivate", null);
        Assert.Equal(HttpStatusCode.OK, reactivate.StatusCode);

        var listedAfterReactivate = await admin.GetFromJsonAsync<List<AgentSummaryDto>>(
            $"/api/admin/agents/{NovAccesApiFactory.TestSite}", Json);
        Assert.True(listedAfterReactivate!.Single(a => a.Matricule == matricule).IsActive);

        // Matricule inconnu sur ce site : 404, pas d'exception.
        var unknown = await admin.PostAsync(
            $"/api/admin/agents/{NovAccesApiFactory.TestSite}/inconnu-{Guid.NewGuid():N}/reactivate", null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    /// <summary>
    /// Désactiver un site coupe l'accès (403, message explicite) SANS toucher
    /// aux données, pour tous les comptes rattachés — pas seulement les
    /// nouveaux. La réactivation reste réservée au SuperAdmin, comme pour un
    /// compte. Touche TenantResolutionMiddleware (zone sensible, CLAUDE.md §7.3).
    /// </summary>
    [SkippableFact]
    public async Task DeactivateSite_BlocksTenantAccess_ReactivationRequiresSuperAdmin()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var siteId = $"dsite{Guid.NewGuid():N}".Substring(0, 20);
        var superAdmin = await AdminClientAsync();
        try
        {
            var provision = await superAdmin.PostAsJsonAsync("/api/admin/sites", new ProvisionSiteRequestDto(siteId));
            Assert.Equal(HttpStatusCode.OK, provision.StatusCode);

            // Hôte rattaché au site, accès normal avant désactivation.
            var email = $"hote-{Guid.NewGuid():N}@{siteId}.local";
            const string password = "Test!Passw0rd2026";
            var register = await superAdmin.PostAsJsonAsync("/api/auth/register",
                new RegisterUserRequestDto(email, password, "Hôte Test", "Hote", siteId));
            Assert.Equal(HttpStatusCode.OK, register.StatusCode);

            var hote = _factory.CreateClient();
            var login = await hote.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(email, password));
            login.EnsureSuccessStatusCode();
            var token = (await login.Content.ReadFromJsonAsync<LoginResponseDto>(Json))!.AccessToken;
            hote.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var beforeDeactivation = await hote.GetAsync("/api/visits/mine");
            Assert.Equal(HttpStatusCode.OK, beforeDeactivation.StatusCode);

            // Motif trop court : refusé, même hiérarchie de validation qu'un compte.
            var badReason = await superAdmin.PostAsJsonAsync(
                $"/api/admin/sites/{siteId}/deactivate", new DeactivateSiteRequestDto("x"));
            Assert.Equal(HttpStatusCode.BadRequest, badReason.StatusCode);

            var deactivate = await superAdmin.PostAsJsonAsync(
                $"/api/admin/sites/{siteId}/deactivate", new DeactivateSiteRequestDto("Contrat non reconduit (test)"));
            Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

            // Un compte DÉJÀ existant sur ce site perd l'accès immédiatement —
            // pas seulement les nouveaux rattachements.
            var afterDeactivation = await hote.GetAsync("/api/visits/mine");
            Assert.Equal(HttpStatusCode.Forbidden, afterDeactivation.StatusCode);
            var body = await afterDeactivation.Content.ReadAsStringAsync();
            Assert.Contains("désactivé", body, StringComparison.OrdinalIgnoreCase);

            // Redondant : refuse une seconde désactivation.
            var alreadyDeactivated = await superAdmin.PostAsJsonAsync(
                $"/api/admin/sites/{siteId}/deactivate", new DeactivateSiteRequestDto("Nouvelle tentative"));
            Assert.Equal(HttpStatusCode.Conflict, alreadyDeactivated.StatusCode);

            // Un Admin ordinaire (pas SuperAdmin) ne peut pas réactiver.
            var plainAdminEmail = $"admin-target-{Guid.NewGuid():N}@sicopa.local";
            var registerAdmin = await superAdmin.PostAsJsonAsync("/api/auth/register",
                new RegisterUserRequestDto(plainAdminEmail, password, "Admin cible", "Admin", null));
            Assert.Equal(HttpStatusCode.OK, registerAdmin.StatusCode);
            var plainAdmin = _factory.CreateClient();
            var adminLogin = await plainAdmin.PostAsJsonAsync(
                "/api/auth/login", new LoginRequestDto(plainAdminEmail, password));
            adminLogin.EnsureSuccessStatusCode();
            var adminToken = (await adminLogin.Content.ReadFromJsonAsync<LoginResponseDto>(Json))!.AccessToken;
            plainAdmin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

            var forbiddenReactivate = await plainAdmin.PostAsync($"/api/admin/sites/{siteId}/reactivate", null);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenReactivate.StatusCode);

            var reactivate = await superAdmin.PostAsync($"/api/admin/sites/{siteId}/reactivate", null);
            Assert.Equal(HttpStatusCode.OK, reactivate.StatusCode);

            var afterReactivation = await hote.GetAsync("/api/visits/mine");
            Assert.Equal(HttpStatusCode.OK, afterReactivation.StatusCode);
        }
        finally
        {
            await DropSchemaAsync($"site_{siteId}");
        }
    }

    /// <summary>
    /// Exception étroite au blocage d'un site désactivé : Admin/SuperAdmin
    /// gardent une lecture seule (consultation de conformité), jamais
    /// l'écriture, et jamais les autres rôles (Sûreté), même en lecture, même
    /// sur leur propre site rattaché. Touche TenantResolutionMiddleware
    /// (zone sensible, CLAUDE.md §7.3).
    /// </summary>
    [SkippableFact]
    public async Task DeactivatedSite_AllowsReadOnlyForAdmin_ButNeverWrites_NorOtherRoles()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var siteId = $"rosite{Guid.NewGuid():N}".Substring(0, 20);
        var superAdmin = await AdminClientAsync();
        try
        {
            var provision = await superAdmin.PostAsJsonAsync("/api/admin/sites", new ProvisionSiteRequestDto(siteId));
            Assert.Equal(HttpStatusCode.OK, provision.StatusCode);

            // Un compte Sûreté rattaché au site, pour vérifier qu'il perd TOUT
            // accès (même lecture) une fois le site désactivé.
            var sureteEmail = $"surete-{Guid.NewGuid():N}@{siteId}.local";
            const string password = "Test!Passw0rd2026";
            var registerSurete = await superAdmin.PostAsJsonAsync("/api/auth/register",
                new RegisterUserRequestDto(sureteEmail, password, "Sûreté Test", "Surete", siteId));
            Assert.Equal(HttpStatusCode.OK, registerSurete.StatusCode);
            var surete = _factory.CreateClient();
            var sureteLogin = await surete.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(sureteEmail, password));
            sureteLogin.EnsureSuccessStatusCode();
            var sureteToken = (await sureteLogin.Content.ReadFromJsonAsync<LoginResponseDto>(Json))!.AccessToken;
            surete.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sureteToken);

            var deactivate = await superAdmin.PostAsJsonAsync(
                $"/api/admin/sites/{siteId}/deactivate", new DeactivateSiteRequestDto("Test lecture seule"));
            Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

            // SuperAdmin : lecture (GET, via X-Site-Id) autorisée malgré la désactivation.
            using var readRequest = new HttpRequestMessage(HttpMethod.Get, "/api/exclusions");
            readRequest.Headers.Add("X-Site-Id", siteId);
            var read = await superAdmin.SendAsync(readRequest);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);

            // SuperAdmin : écriture (POST) toujours refusée, même en lecture seule autorisée.
            using var writeRequest = new HttpRequestMessage(HttpMethod.Post, "/api/exclusions")
            {
                Content = JsonContent.Create(new AddExclusionRequestDto("Test Écriture", "Test"))
            };
            writeRequest.Headers.Add("X-Site-Id", siteId);
            var write = await superAdmin.SendAsync(writeRequest);
            Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

            // Sûreté : bloquée même en lecture, même sur SON propre site (claim, pas X-Site-Id).
            var sureteRead = await surete.GetAsync("/api/exclusions");
            Assert.Equal(HttpStatusCode.Forbidden, sureteRead.StatusCode);
        }
        finally
        {
            await DropSchemaAsync($"site_{siteId}");
        }
    }

    /// <summary>
    /// Export d'un site (restitution contractuelle) : renvoie un ZIP contenant
    /// visites/journal/audit en CSV, fonctionne sur un site actif comme
    /// désactivé, refusé pour un rôle non-Admin.
    /// </summary>
    [SkippableFact]
    public async Task ExportSite_ReturnsZipWithThreeCsvFiles_ActiveOrDeactivated()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var siteId = $"exsite{Guid.NewGuid():N}".Substring(0, 20);
        var superAdmin = await AdminClientAsync();
        try
        {
            var provision = await superAdmin.PostAsJsonAsync("/api/admin/sites", new ProvisionSiteRequestDto(siteId));
            Assert.Equal(HttpStatusCode.OK, provision.StatusCode);

            // Export d'un site encore ACTIF : doit déjà fonctionner.
            var exportActive = await superAdmin.GetAsync($"/api/admin/sites/{siteId}/export");
            Assert.Equal(HttpStatusCode.OK, exportActive.StatusCode);
            Assert.Equal("application/zip", exportActive.Content.Headers.ContentType?.MediaType);
            AssertZipContainsExpectedEntries(await exportActive.Content.ReadAsByteArrayAsync());

            // Un rôle non-Admin (Sûreté) ne peut pas exporter.
            var surete = await NewUserClientAsync("Surete");
            var forbidden = await surete.GetAsync($"/api/admin/sites/{siteId}/export");
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

            // Export après désactivation : doit continuer de fonctionner
            // (c'est précisément le cas d'usage — restituer les données à la
            // fin d'un contrat).
            var deactivate = await superAdmin.PostAsJsonAsync(
                $"/api/admin/sites/{siteId}/deactivate", new DeactivateSiteRequestDto("Test export"));
            Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

            var exportDeactivated = await superAdmin.GetAsync($"/api/admin/sites/{siteId}/export");
            Assert.Equal(HttpStatusCode.OK, exportDeactivated.StatusCode);
            AssertZipContainsExpectedEntries(await exportDeactivated.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            await DropSchemaAsync($"site_{siteId}");
        }
    }

    private static void AssertZipContainsExpectedEntries(byte[] zipBytes)
    {
        using var stream = new MemoryStream(zipBytes);
        using var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.Name).ToList();
        Assert.Contains("visites.csv", names);
        Assert.Contains("journal-scans.csv", names);
        Assert.Contains("audit-administration.csv", names);
    }

    // ---- Aides ----

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequestDto(NovAccesApiFactory.AdminEmail, NovAccesApiFactory.AdminPassword));
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginResponseDto>(Json))!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<HttpClient> NewUserClientAsync(string role)
    {
        var admin = await AdminClientAsync();
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

    /// <summary>
    /// Construit une demande d'activation complète, PREUVE DE POSSESSION
    /// comprise : le device signe « ticket|deviceInstanceId » avec sa clé
    /// privée. Reproduit exactement ce que fait l'app mobile (EnrollmentPage).
    /// </summary>
    private static DeviceEnrollmentRequestDto EnrollmentRequest(
        ECDsa deviceKey, string ticket, string deviceInstanceId)
        => new(ticket, deviceInstanceId, deviceKey.ExportSubjectPublicKeyInfoPem(),
            SignProof(deviceKey, ticket, deviceInstanceId));

    private static string SignProof(ECDsa deviceKey, string ticket, string deviceInstanceId)
    {
        var signature = deviceKey.SignData(
            Encoding.UTF8.GetBytes($"{ticket}|{deviceInstanceId}"), HashAlgorithmName.SHA256);
        return Convert.ToBase64String(signature).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string TicketFromQr(string payload)
    {
        var marker = "ticket=";
        var start = payload.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += marker.Length;
        var end = payload.IndexOf('&', start);
        var encoded = end >= 0 ? payload[start..end] : payload[start..];
        return Uri.UnescapeDataString(encoded);
    }

    private static async Task<bool> SchemaHasTableAsync(string schema, string table)
    {
        await using var conn = new NpgsqlConnection(NovAccesApiFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pg_tables WHERE schemaname = @s AND tablename = @t";
        cmd.Parameters.AddWithValue("s", schema);
        cmd.Parameters.AddWithValue("t", table);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    private static async Task DropSchemaAsync(string schema)
    {
        try
        {
            await using var conn = new NpgsqlConnection(NovAccesApiFactory.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* nettoyage best-effort */ }
    }
}
