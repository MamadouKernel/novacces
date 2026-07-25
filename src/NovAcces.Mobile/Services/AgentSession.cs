using NovAcces.Shared.Dtos;
using NovAcces.Shared.Offline;

namespace NovAcces.Mobile.Services;

/// <summary>
/// État runtime du poste agent : sens du poste (Entrée/Sortie), liste hors-ligne
/// signée mise en cache, et file PERSISTÉE des scans hors-ligne en attente de
/// resynchronisation (SQLite, survit à un redémarrage). Le vérificateur ES256
/// (clé publique) est conservé ici.
/// </summary>
public sealed class AgentSession
{
    private readonly AgentApiClient _api;
    private readonly OfflineScanStore _store;
    private readonly OfflineQrVerifier _verifier;

    public AgentSession(AgentApiClient api, AgentConfig config, OfflineScanStore store)
    {
        _api = api;
        _store = store;
        _verifier = new OfflineQrVerifier(config.PublicKeyPem);
    }

    /// <summary>Sens du poste. Bascule Entrée ⇄ Sortie toujours visible (§11).</summary>
    public string Direction { get; set; } = "Entry";

    public OfflineQrVerifier Verifier => _verifier;

    /// <summary>Dernière liste hors-ligne vérifiée (null si jamais chargée).</summary>
    public OfflineListResult? OfflineList { get; private set; }

    /// <summary>Met en file (persistée) un scan réalisé hors ligne.</summary>
    public Task EnqueueOfflineScanAsync(OfflineScanDto scan) => _store.EnqueueAsync(scan);

    /// <summary>Nombre de scans hors-ligne en attente de resynchronisation.</summary>
    public Task<int> PendingCountAsync() => _store.CountAsync();

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

    /// <summary>
    /// Confronte les scans hors-ligne persistés au registre central et vide la
    /// file locale si l'opération aboutit. Retourne les conflits éventuels
    /// (ex. QR révoqué pendant la coupure = événement de sécurité).
    /// </summary>
    public async Task<ResyncResultDto?> ResyncAsync(CancellationToken ct = default)
    {
        var pending = await _store.GetAllAsync();
        if (pending.Count == 0)
            return new ResyncResultDto(0, Array.Empty<ResyncConflictDto>());

        var result = await _api.ResyncAsync(pending, ct);
        if (result is not null)
            await _store.ClearAsync();
        return result;
    }
}
