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

    /// <summary>Scan nominal (en ligne) : l'API applique toute la logique de sûreté.</summary>
    public async Task<ScanResponseDto?> ScanAsync(string signedQr, string direction, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/scan",
            new ScanRequestDto(signedQr, direction, "terminal"), ct);
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
