using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    public async Task ProvisionSite_ForbiddenForNonAdmin()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var surete = await NewUserClientAsync("Surete");
        var resp = await surete.PostAsJsonAsync("/api/admin/sites", new ProvisionSiteRequestDto("whatever"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
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
