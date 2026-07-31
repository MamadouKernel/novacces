using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
            new DeviceEnrollmentRequestDto(TicketFromQr(ticket.QrPayload), deviceId, ecdsa.ExportSubjectPublicKeyInfoPem()));
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
            new DeviceEnrollmentRequestDto(TicketFromQr(ticket.QrPayload), Guid.NewGuid().ToString("D"), ecdsa.ExportSubjectPublicKeyInfoPem()));
        Assert.Equal(HttpStatusCode.Gone, second.StatusCode);

        // Un nouveau ticket réenrôle le terminal et invalide la clé précédente.
        var replacementTicketResponse = await admin.PostAsync($"/api/admin/terminals/{created!.Id}/enrollment-ticket", null);
        var replacementTicket = await replacementTicketResponse.Content.ReadFromJsonAsync<EnrollmentTicketResponseDto>(Json);
        var replacement = await _factory.CreateClient().PostAsJsonAsync(
            "/api/device-enrollments/activate",
            new DeviceEnrollmentRequestDto(TicketFromQr(replacementTicket!.QrPayload), Guid.NewGuid().ToString("D"), ecdsa.ExportSubjectPublicKeyInfoPem()));
        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);

        var oldKey = _factory.CreateClient();
        oldKey.DefaultRequestHeaders.Add("X-Api-Key", activated.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldKey.GetAsync("/api/agent/sites")).StatusCode);
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
            new DeviceEnrollmentRequestDto(TicketFromQr(ticket!.QrPayload), Guid.NewGuid().ToString("D"), ecdsa.ExportSubjectPublicKeyInfoPem()));
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
