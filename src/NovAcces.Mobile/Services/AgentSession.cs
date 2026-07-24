using NovAcces.Shared.Dtos;
using NovAcces.Shared.Offline;

namespace NovAcces.Mobile.Services;

/// <summary>
/// État runtime du poste agent : sens du poste (Entrée/Sortie), connectivité,
/// liste hors-ligne signée mise en cache, et file des scans hors-ligne en attente
/// de resynchronisation. Le vérificateur ES256 (clé publique) est conservé ici.
/// </summary>
public sealed class AgentSession
{
    private readonly AgentApiClient _api;
    private readonly OfflineQrVerifier _verifier;

    public AgentSession(AgentApiClient api, AgentConfig config)
    {
        _api = api;
        _verifier = new OfflineQrVerifier(config.PublicKeyPem);
    }

    /// <summary>Sens du poste. Bascule Entrée ⇄ Sortie toujours visible (§11).</summary>
    public string Direction { get; set; } = "Entry";

    public OfflineQrVerifier Verifier => _verifier;

    /// <summary>Dernière liste hors-ligne vérifiée (null si jamais chargée).</summary>
    public OfflineListResult? OfflineList { get; private set; }

    /// <summary>Scans réalisés hors ligne, en attente de resynchronisation.</summary>
    public List<OfflineScanDto> PendingOfflineScans { get; } = new();

    /// <summary>
    /// Charge (en ligne) la liste hors-ligne signée du jour et la vérifie
    /// localement. À appeler au démarrage et périodiquement tant qu'on est en
    /// ligne, pour préparer une éventuelle coupure.
    /// </summary>
    public async Task RefreshOfflineListAsync(CancellationToken ct = default)
    {
        var dto = await _api.GetOfflineListAsync(ct);
        if (dto is not null)
            OfflineList = _verifier.VerifyDailyList(dto.SignedList, DateTimeOffset.UtcNow);
    }

    /// <summary>Confronte les scans hors-ligne au registre et vide la file si OK.</summary>
    public async Task<ResyncResultDto?> ResyncAsync(CancellationToken ct = default)
    {
        if (PendingOfflineScans.Count == 0)
            return new ResyncResultDto(0, Array.Empty<ResyncConflictDto>());

        var result = await _api.ResyncAsync(PendingOfflineScans, ct);
        if (result is not null)
            PendingOfflineScans.Clear();
        return result;
    }
}
