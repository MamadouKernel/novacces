using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using NovAcces.Shared.Dtos;
using Xunit;

namespace NovAcces.IntegrationTests.Api;

/// <summary>
/// Journal d'audit des actions d'administration/sûreté (§8.5) : chaque action
/// privilégiée (ici une révocation) y est inscrite ; le journal est inaltérable
/// au niveau base (UPDATE/DELETE bloqués) et sa consultation est réservée à la
/// Sûreté/Admin.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuditTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NovAccesApiFactory _factory;

    public AuditTests(NovAccesApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task RevokingVisit_IsRecorded_AndVisibleToSurete()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        // Un hôte crée une demande puis la révoque.
        var hote = await LoginNewUserAsync("Hote");
        var create = await hote.PostAsJsonAsync("/api/visits", new CreateVisitRequestDto(
            $"Audit-{Guid.NewGuid():N}", "ACME", "Test", "Unique", DateTimeOffset.UtcNow, 60, null, null));
        create.EnsureSuccessStatusCode();
        var visitId = (await create.Content.ReadFromJsonAsync<CreateVisitResponseDto>(Json))!.VisitId;

        var revoke = await hote.PostAsync($"/api/visits/{visitId}/revoke", null);
        revoke.EnsureSuccessStatusCode();

        // La Sûreté consulte le journal d'audit : la révocation y figure.
        var surete = await LoginNewUserAsync("Surete");
        var resp = await surete.GetAsync("/api/audit");
        resp.EnsureSuccessStatusCode();
        var entries = await resp.Content.ReadFromJsonAsync<List<AdminAuditDto>>(Json);

        Assert.Contains(entries!, e => e.Action == "VisitRevoked" && e.TargetId == visitId.ToString());
    }

    [SkippableFact]
    public async Task AuditJournal_IsAppendOnly_UpdateAndDeleteAreBlocked()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        // On s'assure qu'au moins une entrée existe (une révocation).
        var hote = await LoginNewUserAsync("Hote");
        var create = await hote.PostAsJsonAsync("/api/visits", new CreateVisitRequestDto(
            $"AuditImmut-{Guid.NewGuid():N}", "ACME", "Test", "Unique", DateTimeOffset.UtcNow, 60, null, null));
        create.EnsureSuccessStatusCode();
        var visitId = (await create.Content.ReadFromJsonAsync<CreateVisitResponseDto>(Json))!.VisitId;
        (await hote.PostAsync($"/api/visits/{visitId}/revoke", null)).EnsureSuccessStatusCode();

        var table = $"\"site_{NovAccesApiFactory.TestSite}\".admin_audit";

        // UPDATE et DELETE doivent être rejetés par le trigger append-only.
        await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync($"UPDATE {table} SET \"Detail\" = 'falsifié'"));
        await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync($"DELETE FROM {table}"));
    }

    [SkippableFact]
    public async Task AuditConsultation_IsForbiddenForHote()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var hote = await LoginNewUserAsync("Hote");
        var resp = await hote.GetAsync("/api/audit");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ---- Aides ----

    private static async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(NovAccesApiFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
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
