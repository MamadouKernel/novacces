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
        // JSON {"ticket": "...", "baseUrl": "..."} — format attendu par le
        // parseur de l'app agent réelle (React Native, parseEnrollmentTicket),
        // PAS l'ancien schéma d'URI "novacces://enroll?..." du prototype MAUI
        // abandonné.
        Assert.Contains("\"ticket\"", ticket!.QrPayload);
        Assert.Contains("\"baseUrl\"", ticket.QrPayload);
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
    public async Task PosteEnrollmentTicket_IsReusableAndCreatesSeparateTerminals()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var admin = await AdminClientAsync();
        var label = $"Poste-{Guid.NewGuid():N}";
        var ticketResponse = await admin.PostAsJsonAsync("/api/admin/terminals/poste-enrollment-ticket",
            new CreatePosteEnrollmentTicketRequestDto(label, new[] { NovAccesApiFactory.TestSite }, "entry"));
        Assert.Equal(HttpStatusCode.OK, ticketResponse.StatusCode);
        var ticket = await ticketResponse.Content.ReadFromJsonAsync<EnrollmentTicketResponseDto>(Json);
        Assert.NotNull(ticket);
        // Aucun terminal précréé pour un ticket de poste — contrairement au
        // flux historique (CreateTerminalAsync + enrollment-ticket).
        Assert.Null(ticket!.TerminalId);
        var rawTicket = TicketFromQr(ticket.QrPayload);

        using var ecdsa1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var device1 = Guid.NewGuid().ToString("D");
        var activation1 = await _factory.CreateClient().PostAsJsonAsync(
            "/api/device-enrollments/activate", EnrollmentRequest(ecdsa1, rawTicket, device1));
        Assert.Equal(HttpStatusCode.OK, activation1.StatusCode);
        var activated1 = await activation1.Content.ReadFromJsonAsync<DeviceEnrollmentActivationDto>(Json);
        Assert.NotNull(activated1);

        // MÊME ticket, DEUXIÈME appareil : doit fonctionner (réutilisable),
        // contrairement au ticket historique (410 Gone au second scan — voir
        // TerminalEnrollmentTicket_IsOneTimeAndActivatesDevice ci-dessus).
        using var ecdsa2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var device2 = Guid.NewGuid().ToString("D");
        var activation2 = await _factory.CreateClient().PostAsJsonAsync(
            "/api/device-enrollments/activate", EnrollmentRequest(ecdsa2, rawTicket, device2));
        Assert.Equal(HttpStatusCode.OK, activation2.StatusCode);
        var activated2 = await activation2.Content.ReadFromJsonAsync<DeviceEnrollmentActivationDto>(Json);
        Assert.NotNull(activated2);

        // Deux TERMINAUX DISTINCTS, chacun sa propre clé, même gabarit (label/poste).
        Assert.NotEqual(activated1!.TerminalId, activated2!.TerminalId);
        Assert.NotEqual(activated1.ApiKey, activated2.ApiKey);

        var listed = await admin.GetFromJsonAsync<List<TerminalSummaryDto>>("/api/admin/terminals", Json);
        var t1 = listed!.Single(t => t.Id == activated1.TerminalId);
        var t2 = listed!.Single(t => t.Id == activated2.TerminalId);
        Assert.Equal(label, t1.Label);
        Assert.Equal(label, t2.Label);
        Assert.Equal("entry", t1.CheckpointId);
        Assert.Equal("entry", t2.CheckpointId);

        // Chaque clé fonctionne indépendamment de l'autre.
        var client1 = _factory.CreateClient();
        client1.DefaultRequestHeaders.Add("X-Api-Key", activated1.ApiKey);
        Assert.Equal(HttpStatusCode.OK, (await client1.GetAsync("/api/agent/sites")).StatusCode);

        var client2 = _factory.CreateClient();
        client2.DefaultRequestHeaders.Add("X-Api-Key", activated2.ApiKey);
        Assert.Equal(HttpStatusCode.OK, (await client2.GetAsync("/api/agent/sites")).StatusCode);

        // Le MÊME appareil ne peut pas rescanner pour créer un doublon fantôme.
        var replay = await _factory.CreateClient().PostAsJsonAsync(
            "/api/device-enrollments/activate", EnrollmentRequest(ecdsa1, rawTicket, device1));
        Assert.Equal(HttpStatusCode.Gone, replay.StatusCode);
    }

    [SkippableFact]
    public async Task TerminalEnrollmentTicket_ManualCode_ActivatesSameTicketAsQr()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var admin = await AdminClientAsync();
        var create = await admin.PostAsJsonAsync("/api/admin/terminals",
            new CreateTerminalRequestDto($"Manuel-{Guid.NewGuid():N}", new[] { NovAccesApiFactory.TestSite }));
        var created = await create.Content.ReadFromJsonAsync<CreateTerminalResponseDto>(Json);
        Assert.NotNull(created);

        var ticketResponse = await admin.PostAsync($"/api/admin/terminals/{created!.Id}/enrollment-ticket", null);
        var ticket = await ticketResponse.Content.ReadFromJsonAsync<EnrollmentTicketResponseDto>(Json);
        Assert.NotNull(ticket);
        Assert.False(string.IsNullOrWhiteSpace(ticket!.ManualCode));
        Assert.NotEqual(ticket.ManualCode, TicketFromQr(ticket.QrPayload));

        // Casse volontairement "sale" (minuscules) : la normalisation du hash
        // doit se comporter comme pour le code de secours visiteur. Pas
        // d'espaces ajoutés ici : la preuve de possession signe la valeur
        // TELLE QUE présentée (après Trim() côté serveur comme côté mobile),
        // donc le test doit signer exactement ce qui sera comparé.
        var typedCode = ticket.ManualCode.ToLowerInvariant();

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var deviceId = Guid.NewGuid().ToString("D");
        var activation = await _factory.CreateClient().PostAsJsonAsync(
            "/api/device-enrollments/activate",
            EnrollmentRequest(ecdsa, typedCode, deviceId));
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
        var activated = await activation.Content.ReadFromJsonAsync<DeviceEnrollmentActivationDto>(Json);
        Assert.NotNull(activated);

        var terminal = _factory.CreateClient();
        terminal.DefaultRequestHeaders.Add("X-Api-Key", activated!.ApiKey);
        Assert.Equal(HttpStatusCode.OK, (await terminal.GetAsync("/api/agent/sites")).StatusCode);

        // Le code manuel et le QR pointent vers le MÊME ticket : une fois
        // consommé par le code, le QR (jamais utilisé) est lui aussi mort.
        var viaQrAfter = await _factory.CreateClient().PostAsJsonAsync(
            "/api/device-enrollments/activate",
            EnrollmentRequest(ecdsa, TicketFromQr(ticket.QrPayload), Guid.NewGuid().ToString("D")));
        Assert.Equal(HttpStatusCode.Gone, viaQrAfter.StatusCode);
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
    /// Suppression (archivage) d'un agent : disparaît de la liste normale,
    /// apparaît dans les archivés, refusée tant que l'agent est actif, et son
    /// matricule redevient réutilisable pour un NOUVEL agent.
    /// </summary>
    [SkippableFact]
    public async Task DeleteAgent_RequiresDeactivationFirst_ThenArchivesAndFreesMatricule()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var admin = await AdminClientAsync();
        var matricule = $"AG-{Guid.NewGuid():N}".Substring(0, 12);

        var create = await admin.PostAsJsonAsync("/api/admin/agents",
            new CreateAgentRequestDto(NovAccesApiFactory.TestSite, matricule, "Agent Test Suppression", "1234"));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        // Refusé tant que l'agent est encore actif (discipline en deux temps).
        var deleteWhileActive = await admin.PostAsync(
            $"/api/admin/agents/{NovAccesApiFactory.TestSite}/{matricule}/delete", null);
        Assert.Equal(HttpStatusCode.Conflict, deleteWhileActive.StatusCode);

        var deactivate = await admin.PostAsync(
            $"/api/admin/agents/{NovAccesApiFactory.TestSite}/{matricule}/deactivate", null);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var delete = await admin.PostAsync(
            $"/api/admin/agents/{NovAccesApiFactory.TestSite}/{matricule}/delete", null);
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        // Disparu de la liste normale...
        var listed = await admin.GetFromJsonAsync<List<AgentSummaryDto>>(
            $"/api/admin/agents/{NovAccesApiFactory.TestSite}", Json);
        Assert.DoesNotContain(listed!, a => a.Matricule == matricule);

        // ...mais visible dans les archivés, avec qui/quand.
        var archived = await admin.GetFromJsonAsync<List<ArchivedAgentSummaryDto>>(
            $"/api/admin/agents/{NovAccesApiFactory.TestSite}/archived", Json);
        // DeletedBy porte l'identifiant technique de l'acteur (sub du JWT,
        // voir ClaimsPrincipal.HostIdentifier()), pas son email.
        var archivedEntry = Assert.Single(archived!, a => a.Matricule == matricule);
        Assert.False(string.IsNullOrWhiteSpace(archivedEntry.DeletedBy));

        // Redondant : un matricule déjà supprimé ne peut pas l'être une seconde fois.
        var deleteAgain = await admin.PostAsync(
            $"/api/admin/agents/{NovAccesApiFactory.TestSite}/{matricule}/delete", null);
        Assert.Equal(HttpStatusCode.NotFound, deleteAgain.StatusCode);

        // Le matricule est réutilisable pour un NOUVEL agent sur le même site.
        var recreate = await admin.PostAsJsonAsync("/api/admin/agents",
            new CreateAgentRequestDto(NovAccesApiFactory.TestSite, matricule, "Agent Test Suppression (reprise)", "5678"));
        Assert.Equal(HttpStatusCode.OK, recreate.StatusCode);

        var listedAfterRecreate = await admin.GetFromJsonAsync<List<AgentSummaryDto>>(
            $"/api/admin/agents/{NovAccesApiFactory.TestSite}", Json);
        Assert.True(listedAfterRecreate!.Single(a => a.Matricule == matricule).IsActive);
    }

    /// <summary>
    /// L'archive des agents est réservée au SuperAdmin — un Admin ordinaire
    /// peut créer/désactiver/supprimer des agents mais pas consulter l'archive.
    /// </summary>
    [SkippableFact]
    public async Task ArchivedAgentsEndpoint_IsForbiddenForPlainAdmin()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var admin = await NewUserClientAsync("Admin");
        var forbidden = await admin.GetAsync($"/api/admin/agents/{NovAccesApiFactory.TestSite}/archived");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    /// <summary>
    /// Un même matricule ne doit JAMAIS être actif sur deux sites en même
    /// temps (traçabilité individuelle des scans, §8.5) — ni par création
    /// directe, ni par réactivation directe sur le second site.
    /// </summary>
    [SkippableFact]
    public async Task CreateOrReactivateAgent_BlockedIfMatriculeActiveOnAnotherSite()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var otherSite = $"agx{Guid.NewGuid():N}".Substring(0, 20);
        var admin = await AdminClientAsync();
        var matricule = $"AG-{Guid.NewGuid():N}".Substring(0, 12);

        try
        {
            var provision = await admin.PostAsJsonAsync("/api/admin/sites", new ProvisionSiteRequestDto(otherSite));
            Assert.Equal(HttpStatusCode.OK, provision.StatusCode);

            var create = await admin.PostAsJsonAsync("/api/admin/agents",
                new CreateAgentRequestDto(NovAccesApiFactory.TestSite, matricule, "Agent Test Croisé", "1234"));
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);

            // Création directe du même matricule sur l'autre site : refusée.
            var crossCreate = await admin.PostAsJsonAsync("/api/admin/agents",
                new CreateAgentRequestDto(otherSite, matricule, "Agent Test Croisé", "5678"));
            Assert.Equal(HttpStatusCode.Conflict, crossCreate.StatusCode);
            var crossCreateBody = await crossCreate.Content.ReadAsStringAsync();
            Assert.Contains(NovAccesApiFactory.TestSite, crossCreateBody, StringComparison.OrdinalIgnoreCase);

            var listedOtherSite = await admin.GetFromJsonAsync<List<AgentSummaryDto>>(
                $"/api/admin/agents/{otherSite}", Json);
            Assert.DoesNotContain(listedOtherSite!, a => a.Matricule == matricule);

            // Même refus en passant par une réactivation directe : on
            // construit un historique réaliste (le matricule a existé,
            // inactif, sur otherSite AVANT d'être repris sur TestSite) pour
            // que la réactivation trouve bien un enregistrement local — le
            // garde-fou cross-site doit refuser malgré tout.
            var deactivateOnSource = await admin.PostAsync(
                $"/api/admin/agents/{NovAccesApiFactory.TestSite}/{matricule}/deactivate", null);
            Assert.Equal(HttpStatusCode.OK, deactivateOnSource.StatusCode);

            var createOnOther = await admin.PostAsJsonAsync("/api/admin/agents",
                new CreateAgentRequestDto(otherSite, matricule, "Agent Test Croisé", "5678"));
            Assert.Equal(HttpStatusCode.OK, createOnOther.StatusCode);

            var deactivateOnOther = await admin.PostAsync(
                $"/api/admin/agents/{otherSite}/{matricule}/deactivate", null);
            Assert.Equal(HttpStatusCode.OK, deactivateOnOther.StatusCode);

            var reclaimSource = await admin.PostAsync(
                $"/api/admin/agents/{NovAccesApiFactory.TestSite}/{matricule}/reactivate", null);
            Assert.Equal(HttpStatusCode.OK, reclaimSource.StatusCode);

            // Le matricule est maintenant actif sur TestSite ET existe
            // (inactif) sur otherSite : le réactiver là-bas doit être refusé.
            var crossReactivate = await admin.PostAsync(
                $"/api/admin/agents/{otherSite}/{matricule}/reactivate", null);
            Assert.Equal(HttpStatusCode.Conflict, crossReactivate.StatusCode);

            var listedOtherSiteAfter = await admin.GetFromJsonAsync<List<AgentSummaryDto>>(
                $"/api/admin/agents/{otherSite}", Json);
            Assert.False(listedOtherSiteAfter!.Single(a => a.Matricule == matricule).IsActive);
        }
        finally
        {
            await DropSchemaAsync($"site_{otherSite}");
        }
    }

    /// <summary>
    /// Réaffectation atomique : l'agent quitte la source et apparaît actif
    /// sur la cible, jamais actif aux deux endroits, même si un problème
    /// survient après la désactivation de la source (compensation).
    /// </summary>
    [SkippableFact]
    public async Task ReassignAgent_MovesAtomically_NeverActiveOnBothSites()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var targetSite = $"agy{Guid.NewGuid():N}".Substring(0, 20);
        var admin = await AdminClientAsync();
        var matricule = $"AG-{Guid.NewGuid():N}".Substring(0, 12);

        try
        {
            var provision = await admin.PostAsJsonAsync("/api/admin/sites", new ProvisionSiteRequestDto(targetSite));
            Assert.Equal(HttpStatusCode.OK, provision.StatusCode);

            var create = await admin.PostAsJsonAsync("/api/admin/agents",
                new CreateAgentRequestDto(NovAccesApiFactory.TestSite, matricule, "Agent Test Réaffectation", "1234"));
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);

            // Cas nominal : réaffectation réussie.
            var reassign = await admin.PostAsJsonAsync(
                $"/api/admin/agents/{NovAccesApiFactory.TestSite}/{matricule}/reassign",
                new ReassignAgentRequestDto(targetSite, "5678"));
            Assert.Equal(HttpStatusCode.OK, reassign.StatusCode);

            var listedSource = await admin.GetFromJsonAsync<List<AgentSummaryDto>>(
                $"/api/admin/agents/{NovAccesApiFactory.TestSite}", Json);
            Assert.False(listedSource!.Single(a => a.Matricule == matricule).IsActive);

            var listedTarget = await admin.GetFromJsonAsync<List<AgentSummaryDto>>(
                $"/api/admin/agents/{targetSite}", Json);
            Assert.True(listedTarget!.Single(a => a.Matricule == matricule).IsActive);

            // Cas d'échec (compensation) : un second matricule, déjà présent
            // (même inactif) sur la cible, fait échouer AddAsync côté cible
            // (unicité par site) APRÈS que la source a déjà été désactivée.
            // La source doit alors être réactivée automatiquement — jamais
            // bloquée nulle part par accident.
            var matricule2 = $"AG-{Guid.NewGuid():N}".Substring(0, 12);

            var seedOnTarget = await admin.PostAsJsonAsync("/api/admin/agents",
                new CreateAgentRequestDto(targetSite, matricule2, "Agent Test Collision", "1111"));
            Assert.Equal(HttpStatusCode.OK, seedOnTarget.StatusCode);
            var deactivateSeed = await admin.PostAsync(
                $"/api/admin/agents/{targetSite}/{matricule2}/deactivate", null);
            Assert.Equal(HttpStatusCode.OK, deactivateSeed.StatusCode);

            var createOnSource = await admin.PostAsJsonAsync("/api/admin/agents",
                new CreateAgentRequestDto(NovAccesApiFactory.TestSite, matricule2, "Agent Test Collision", "2222"));
            Assert.Equal(HttpStatusCode.OK, createOnSource.StatusCode);

            var conflictingReassign = await admin.PostAsJsonAsync(
                $"/api/admin/agents/{NovAccesApiFactory.TestSite}/{matricule2}/reassign",
                new ReassignAgentRequestDto(targetSite, "9999"));
            Assert.Equal(HttpStatusCode.Conflict, conflictingReassign.StatusCode);

            var listedSourceAfterFailure = await admin.GetFromJsonAsync<List<AgentSummaryDto>>(
                $"/api/admin/agents/{NovAccesApiFactory.TestSite}", Json);
            Assert.True(listedSourceAfterFailure!.Single(a => a.Matricule == matricule2).IsActive,
                "La source doit être réactivée (compensée) puisque la création sur la cible a échoué.");
        }
        finally
        {
            await DropSchemaAsync($"site_{targetSite}");
        }
    }

    /// <summary>
    /// Suppression (archivage) d'un compte : un Admin ordinaire (pas
    /// seulement SuperAdmin) peut supprimer un compte qu'il gère, mais seul
    /// le SuperAdmin peut consulter l'archive. Refusé tant que le compte est
    /// actif ; l'email redevient disponible pour un nouveau compte.
    /// </summary>
    [SkippableFact]
    public async Task DeleteAccount_ByPlainAdmin_ArchivedListRestrictedToSuperAdmin_ThenFreesEmail()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var superAdmin = await AdminClientAsync();
        var admin = await NewUserClientAsync("Admin");
        const string password = "Test!Passw0rd2026";
        var email = $"hote-delete-{Guid.NewGuid():N}@sicopa.local";

        var register = await admin.PostAsJsonAsync("/api/auth/register",
            new RegisterUserRequestDto(email, password, "Hôte À Supprimer", "Hote", NovAccesApiFactory.TestSite));
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        var userId = Assert.Single(
            (await admin.GetFromJsonAsync<List<AdminUserDto>>("/api/admin/users", Json))!,
            u => u.Email == email).Id;

        // Refusé tant que le compte est actif.
        var deleteWhileActive = await admin.PostAsync($"/api/admin/users/{userId}/delete", null);
        Assert.Equal(HttpStatusCode.Conflict, deleteWhileActive.StatusCode);

        var deactivate = await admin.PostAsJsonAsync(
            $"/api/admin/users/{userId}/deactivate", new DeactivateUserRequestDto("Test suppression"));
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        // Un Admin ordinaire peut supprimer (pas réservé au SuperAdmin).
        var delete = await admin.PostAsync($"/api/admin/users/{userId}/delete", null);
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        var listed = await admin.GetFromJsonAsync<List<AdminUserDto>>("/api/admin/users", Json);
        Assert.DoesNotContain(listed!, u => u.Id == userId);

        // L'archive, elle, est réservée au SuperAdmin.
        var forbiddenArchive = await admin.GetAsync("/api/admin/users/archived");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenArchive.StatusCode);

        var archived = await superAdmin.GetFromJsonAsync<List<ArchivedAccountSummaryDto>>(
            "/api/admin/users/archived", Json);
        var archivedEntry = Assert.Single(archived!, u => u.Id == userId);
        Assert.False(string.IsNullOrWhiteSpace(archivedEntry.DeletedBy));

        // L'email redevient disponible pour un nouveau compte.
        var recreate = await admin.PostAsJsonAsync("/api/auth/register",
            new RegisterUserRequestDto(email, password, "Hôte (repris)", "Hote", NovAccesApiFactory.TestSite));
        Assert.Equal(HttpStatusCode.OK, recreate.StatusCode);
    }

    /// <summary>
    /// Suppression (archivage) d'un site : un Admin ordinaire peut supprimer,
    /// seul le SuperAdmin consulte l'archive. Contrairement à un agent ou un
    /// compte, l'identifiant reste réservé pour toujours — reprovisionner ne
    /// le fait PAS réapparaître dans la vue consolidée.
    /// </summary>
    [SkippableFact]
    public async Task DeleteSite_ByPlainAdmin_RemovedFromOverview_IdentifierNeverReusable()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var siteId = $"delsite{Guid.NewGuid():N}".Substring(0, 20);
        var superAdmin = await AdminClientAsync();
        var admin = await NewUserClientAsync("Admin");

        try
        {
            var provision = await admin.PostAsJsonAsync("/api/admin/sites", new ProvisionSiteRequestDto(siteId));
            Assert.Equal(HttpStatusCode.OK, provision.StatusCode);

            var deleteWhileActive = await admin.PostAsync($"/api/admin/sites/{siteId}/delete", null);
            Assert.Equal(HttpStatusCode.Conflict, deleteWhileActive.StatusCode);

            var deactivate = await admin.PostAsJsonAsync(
                $"/api/admin/sites/{siteId}/deactivate", new DeactivateSiteRequestDto("Test suppression site"));
            Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

            var delete = await admin.PostAsync($"/api/admin/sites/{siteId}/delete", null);
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

            var overview = await admin.GetFromJsonAsync<List<AdminSiteOverviewDto>>("/api/admin/overview", Json);
            Assert.DoesNotContain(overview!, s => s.SiteId == siteId);

            var forbiddenArchive = await admin.GetAsync("/api/admin/sites/archived");
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenArchive.StatusCode);

            var archived = await superAdmin.GetFromJsonAsync<List<ArchivedSiteSummaryDto>>(
                "/api/admin/sites/archived", Json);
            var archivedEntry = Assert.Single(archived!, s => s.SiteId == siteId);
            Assert.Equal("Test suppression site", archivedEntry.DeactivationReason);

            // Reprovisionner le même identifiant ne le fait PAS réapparaître :
            // le schéma existe déjà (idempotent), mais la ligne de registre
            // reste supprimée, jamais réactivée silencieusement.
            var reprovision = await admin.PostAsJsonAsync("/api/admin/sites", new ProvisionSiteRequestDto(siteId));
            Assert.Equal(HttpStatusCode.OK, reprovision.StatusCode);

            var overviewAfterReprovision = await admin.GetFromJsonAsync<List<AdminSiteOverviewDto>>("/api/admin/overview", Json);
            Assert.DoesNotContain(overviewAfterReprovision!, s => s.SiteId == siteId);
        }
        finally
        {
            await DropSchemaAsync($"site_{siteId}");
        }
    }

    /// <summary>
    /// Suppression (archivage) d'un terminal : refusée tant qu'il est actif,
    /// réservée à l'archive SuperAdmin pour la consultation, et son device
    /// physique redevient réenrôlable comme un NOUVEAU terminal une fois
    /// supprimé.
    /// </summary>
    [SkippableFact]
    public async Task DeleteTerminal_RequiresRevocationFirst_ArchivedListRestrictedToSuperAdmin_ThenFreesDevice()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var superAdmin = await AdminClientAsync();
        var admin = await NewUserClientAsync("Admin");

        var create = await admin.PostAsJsonAsync("/api/admin/terminals",
            new CreateTerminalRequestDto($"Del-{Guid.NewGuid():N}", new[] { NovAccesApiFactory.TestSite }));
        var created = await create.Content.ReadFromJsonAsync<CreateTerminalResponseDto>(Json);
        Assert.NotNull(created);

        var ticketResponse = await admin.PostAsync($"/api/admin/terminals/{created!.Id}/enrollment-ticket", null);
        var ticket = await ticketResponse.Content.ReadFromJsonAsync<EnrollmentTicketResponseDto>(Json);
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var deviceId = Guid.NewGuid().ToString("D");
        var activation = await _factory.CreateClient().PostAsJsonAsync(
            "/api/device-enrollments/activate",
            EnrollmentRequest(deviceKey, TicketFromQr(ticket!.QrPayload), deviceId));
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);

        // Refusé tant que le terminal est actif.
        var deleteWhileActive = await admin.PostAsync($"/api/admin/terminals/{created.Id}/delete", null);
        Assert.Equal(HttpStatusCode.Conflict, deleteWhileActive.StatusCode);

        var revoke = await admin.PostAsync($"/api/admin/terminals/{created.Id}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var delete = await admin.PostAsync($"/api/admin/terminals/{created.Id}/delete", null);
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        var listed = await admin.GetFromJsonAsync<List<TerminalSummaryDto>>("/api/admin/terminals", Json);
        Assert.DoesNotContain(listed!, t => t.Id == created.Id);

        var forbiddenArchive = await admin.GetAsync("/api/admin/terminals/archived");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenArchive.StatusCode);

        var archived = await superAdmin.GetFromJsonAsync<List<ArchivedTerminalSummaryDto>>(
            "/api/admin/terminals/archived", Json);
        var archivedEntry = Assert.Single(archived!, t => t.Id == created.Id);
        Assert.False(string.IsNullOrWhiteSpace(archivedEntry.DeletedBy));

        // Le device physique est réenrôlable sur un NOUVEAU terminal.
        var create2 = await admin.PostAsJsonAsync("/api/admin/terminals",
            new CreateTerminalRequestDto($"Del2-{Guid.NewGuid():N}", new[] { NovAccesApiFactory.TestSite }));
        var created2 = await create2.Content.ReadFromJsonAsync<CreateTerminalResponseDto>(Json);
        var ticketResponse2 = await admin.PostAsync($"/api/admin/terminals/{created2!.Id}/enrollment-ticket", null);
        var ticket2 = await ticketResponse2.Content.ReadFromJsonAsync<EnrollmentTicketResponseDto>(Json);
        using var deviceKey2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var activation2 = await _factory.CreateClient().PostAsJsonAsync(
            "/api/device-enrollments/activate",
            EnrollmentRequest(deviceKey2, TicketFromQr(ticket2!.QrPayload), deviceId));
        Assert.Equal(HttpStatusCode.OK, activation2.StatusCode);
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
        using var doc = System.Text.Json.JsonDocument.Parse(payload);
        return doc.RootElement.GetProperty("ticket").GetString()!;
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
