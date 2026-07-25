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
/// Rétention / purge des données personnelles (§7.3) : les demandes de visite
/// antérieures à la durée de conservation sont supprimées, MAIS jamais celles
/// d'un visiteur encore présent sur site (la sécurité prime), et jamais les
/// demandes récentes. La purge est tracée au journal d'audit inaltérable (§8.5).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RetentionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NovAccesApiFactory _factory;

    public RetentionTests(NovAccesApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task OldTerminalVisit_IsPurged_RecentAndOnSiteVisits_AreKept()
    {
        Skip.IfNot(_factory.DatabaseAvailable, _factory.SkipReason);

        var since = DateTimeOffset.UtcNow;
        var hote = await LoginNewUserAsync("Hote");

        // 1) Demande ancienne et TERMINÉE (jamais entrée) → doit être purgée.
        var oldTerminal = await CreateVisitAsync(hote);
        await BackdateCreatedAtAsync(oldTerminal, days: 400);

        // 2) Demande RÉCENTE → doit être conservée.
        var recent = await CreateVisitAsync(hote);

        // 3) Demande ancienne mais visiteur ENCORE SUR SITE → doit être conservée.
        var oldOnSite = await CreateVisitAsync(hote);
        await CheckInAsync(oldOnSite);
        await BackdateCreatedAtAsync(oldOnSite, days: 400);

        // Act : une passe de purge (conservation par défaut = 365 jours).
        var retention = _factory.Services.GetRequiredService<IDataRetentionService>();
        await retention.PurgeOnceAsync(CancellationToken.None);

        // Assert : seule la demande ancienne et terminée a disparu.
        Assert.False(await VisitExistsAsync(oldTerminal), "La demande ancienne et terminée aurait dû être purgée.");
        Assert.True(await VisitExistsAsync(recent), "Une demande récente ne doit jamais être purgée.");
        Assert.True(await VisitExistsAsync(oldOnSite), "Un visiteur encore sur site ne doit jamais être purgé.");

        // La purge est inscrite au journal d'audit inaltérable.
        Assert.True(await HasPurgeAuditSinceAsync(since), "La purge aurait dû être tracée au journal d'audit (§8.5).");
    }

    // ---- Aides ----

    private async Task<Guid> CreateVisitAsync(HttpClient hote)
    {
        var create = await hote.PostAsJsonAsync("/api/visits", new CreateVisitRequestDto(
            $"Retention-{Guid.NewGuid():N}", "ACME", "Test", "Unique", DateTimeOffset.UtcNow, 60, null, null));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<CreateVisitResponseDto>(Json);
        return created!.VisitId;
    }

    private async Task CheckInAsync(Guid visitId)
    {
        // On récupère le QR signé de la visite pour la scanner en entrée.
        var payload = await ReadSignedPayloadForCheckInAsync(visitId);

        var agent = _factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Api-Key", NovAccesApiFactory.TestApiKey);
        var scan = await agent.PostAsJsonAsync("/api/scan",
            new ScanRequestDto(payload, "Entry", "ignore"));
        scan.EnsureSuccessStatusCode();
    }

    // Le QR n'est renvoyé qu'à la création ; pour un check-in a posteriori on
    // reconstruit le jeton signé depuis le service de signature réel (même clé
    // que l'API en test), à partir du VisitToken/expiration stockés.
    private async Task<string> ReadSignedPayloadForCheckInAsync(Guid visitId)
    {
        await using var conn = new NpgsqlConnection(NovAccesApiFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT \"VisitToken\", \"ScheduledAt\" FROM \"site_{NovAccesApiFactory.TestSite}\".visits WHERE \"Id\" = @id";
        cmd.Parameters.AddWithValue("id", visitId);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var visitToken = reader.GetGuid(0);
        var scheduledAt = reader.GetFieldValue<DateTimeOffset>(1);

        var signing = _factory.Services.GetRequiredService<IQrSigningService>();
        return signing.SignVisitToken(visitId, visitToken, scheduledAt.AddMinutes(15));
    }

    private static async Task BackdateCreatedAtAsync(Guid visitId, int days)
    {
        await using var conn = new NpgsqlConnection(NovAccesApiFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"UPDATE \"site_{NovAccesApiFactory.TestSite}\".visits " +
            $"SET \"CreatedAt\" = now() - interval '{days} days' WHERE \"Id\" = @id";
        cmd.Parameters.AddWithValue("id", visitId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> VisitExistsAsync(Guid visitId)
    {
        await using var conn = new NpgsqlConnection(NovAccesApiFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT count(*) FROM \"site_{NovAccesApiFactory.TestSite}\".visits WHERE \"Id\" = @id";
        cmd.Parameters.AddWithValue("id", visitId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    private static async Task<bool> HasPurgeAuditSinceAsync(DateTimeOffset since)
    {
        await using var conn = new NpgsqlConnection(NovAccesApiFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT count(*) FROM \"site_{NovAccesApiFactory.TestSite}\".admin_audit " +
            "WHERE \"Action\" = 'DataPurged' AND \"Timestamp\" >= @since";
        cmd.Parameters.AddWithValue("since", since);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
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
