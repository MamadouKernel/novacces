using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NovAcces.Application.Abstractions;
using NovAcces.Shared.Dtos;
using Xunit;

namespace NovAcces.IntegrationTests.Api;

/// <summary>
/// Supervision des dépassements (§7) : un visiteur présent au-delà de sa durée
/// prévue voit son niveau d'alerte s'incrémenter lorsque le scanner passe.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OverstayTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NovAccesApiFactory _factory;

    public OverstayTests(NovAccesApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task Scanner_RaisesOverstayLevel_ForOverstayingVisitor()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        // Visite créée + entrée enregistrée (présent sur site).
        var visitId = await CreateAndCheckInAsync();

        // On antidate l'entrée de 2 h : la durée prévue (60 min) est dépassée.
        await BackdateCheckInAsync(visitId, hours: 2);

        // Un passage du scanner doit détecter le dépassement et lever le niveau 1.
        var scanner = _factory.Services.GetRequiredService<IOverstayScanner>();
        await scanner.ScanOnceAsync(CancellationToken.None);

        var level = await ReadOverstayLevelAsync(visitId);
        Assert.True(level >= 1, $"Niveau de dépassement attendu >= 1, obtenu {level}.");
    }

    // ---- Aides ----

    private async Task<Guid> CreateAndCheckInAsync()
    {
        var hote = await LoginNewUserAsync("Hote");
        var create = await hote.PostAsJsonAsync("/api/visits", new CreateVisitRequestDto(
            $"Overstay-{Guid.NewGuid():N}", "ACME", "Test", "Unique", DateTimeOffset.UtcNow, 60, null, null));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<CreateVisitResponseDto>(Json);

        var agent = _factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Api-Key", NovAccesApiFactory.TestApiKey);
        var scan = await agent.PostAsJsonAsync("/api/scan",
            new ScanRequestDto(created!.SignedQrPayload, "Entry", "ignore"));
        scan.EnsureSuccessStatusCode();

        return created.VisitId;
    }

    private static async Task BackdateCheckInAsync(Guid visitId, int hours)
    {
        await using var conn = new NpgsqlConnection(NovAccesApiFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"UPDATE \"site_{NovAccesApiFactory.TestSite}\".visits " +
            $"SET \"CheckedInAt\" = now() - interval '{hours} hours' WHERE \"Id\" = @id";
        cmd.Parameters.AddWithValue("id", visitId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> ReadOverstayLevelAsync(Guid visitId)
    {
        await using var conn = new NpgsqlConnection(NovAccesApiFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT \"OverstayLevel\" FROM \"site_{NovAccesApiFactory.TestSite}\".visits WHERE \"Id\" = @id";
        cmd.Parameters.AddWithValue("id", visitId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
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
