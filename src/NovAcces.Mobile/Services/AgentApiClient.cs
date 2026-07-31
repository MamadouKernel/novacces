using System.Net.Http.Json;
using NovAcces.Shared.Dtos;

namespace NovAcces.Mobile.Services;

/// <summary>
/// Client de l'API NovAcces pour l'app agent. Joint la clé API du terminal. Ne
/// réimplémente aucune règle de sûreté en mode NOMINAL : c'est l'API qui décide
/// (POST /api/scan). Le mode dégradé, lui, s'appuie sur OfflineScanEvaluator
/// (NovAcces.Shared.Offline) quand le réseau est absent.
/// </summary>
public sealed class AgentApiClient
{
    private readonly HttpClient _http;

    public AgentApiClient(HttpClient http, AgentConfig config)
    {
        _http = http;
        _http.BaseAddress = new Uri(config.ApiBaseUrl);
        _http.DefaultRequestHeaders.Add("X-Api-Key", config.ApiKey);
    }

    /// <summary>Prise de poste : identifie l'agent (matricule + PIN) et ouvre un poste.</summary>
    public async Task<ShiftStartResponseDto?> StartShiftAsync(string matricule, string pin, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/agent/shift/start",
            new ShiftStartRequestDto(matricule, pin), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ShiftStartResponseDto>(cancellationToken: ct);
    }

    /// <summary>
    /// Scan nominal (en ligne) : l'API applique toute la logique de sûreté. Le
    /// jeton de poste (s'il existe) est joint pour tracer le scan au matricule
    /// de l'agent (§8.5).
    /// </summary>
    public async Task<ScanResponseDto?> ScanAsync(string signedQr, string direction, string? shiftToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/scan")
        {
            Content = JsonContent.Create(new ScanRequestDto(signedQr, direction, "terminal")),
        };
        if (!string.IsNullOrWhiteSpace(shiftToken))
            req.Headers.Add("X-Shift-Token", shiftToken);

        using var response = await _http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ScanResponseDto>(cancellationToken: ct);
    }

    /// <summary>Liste des attendus du jour (nom + statut + fenêtre uniquement).</summary>
    public async Task<IReadOnlyList<ExpectedVisitorDto>> GetExpectedTodayAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<ExpectedVisitorDto>>("/api/agent/expected-today", ct)
           ?? new List<ExpectedVisitorDto>();

    /// <summary>Charge la liste hors-ligne signée du jour (à mettre en cache local).</summary>
    public async Task<OfflineListDto?> GetOfflineListAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<OfflineListDto>("/api/agent/offline-list", ct);

    /// <summary>Remonte les scans effectués hors ligne pour confrontation (conflits).</summary>
    public async Task<ResyncResultDto?> ResyncAsync(IReadOnlyList<OfflineScanDto> scans, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/agent/resync", new ResyncRequestDto(scans), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ResyncResultDto>(cancellationToken: ct);
    }
}
