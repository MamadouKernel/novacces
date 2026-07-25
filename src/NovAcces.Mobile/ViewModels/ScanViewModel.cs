using Microsoft.Maui.Networking;
using NovAcces.Mobile.Services;
using NovAcces.Shared.Dtos;
using NovAcces.Shared.Offline;

namespace NovAcces.Mobile.ViewModels;

/// <summary>
/// Orchestration d'un scan → verdict, en gérant le basculement en ligne / hors
/// ligne. En ligne : l'API décide (toute la logique de sûreté serveur). Hors
/// ligne : décision locale via OfflineScanEvaluator (vérification ES256 + liste
/// signée du jour), et le scan est mis en file pour resynchronisation.
/// </summary>
public sealed class ScanViewModel
{
    private readonly AgentApiClient _api;
    private readonly AgentSession _session;
    private readonly IConnectivity _connectivity;

    public ScanViewModel(AgentApiClient api, AgentSession session, IConnectivity connectivity)
    {
        _api = api;
        _session = session;
        _connectivity = connectivity;
    }

    public bool IsOnline => _connectivity.NetworkAccess == NetworkAccess.Internet;

    public async Task<ScanVerdict> EvaluateAsync(string signedQr, CancellationToken ct = default)
    {
        if (IsOnline)
        {
            try
            {
                var r = await _api.ScanAsync(signedQr, _session.Direction, ct);
                if (r is not null)
                    return ScanVerdict.FromApi(r.VerdictCode, r.IsGranted, r.IsCheckOut, r.IsSecurityEvent, r.VisitorName);
            }
            catch
            {
                // Bascule en mode dégradé si l'appel réseau échoue.
            }
        }

        return await EvaluateOfflineAsync(signedQr);
    }

    private async Task<ScanVerdict> EvaluateOfflineAsync(string signedQr)
    {
        var now = DateTimeOffset.UtcNow;
        var list = _session.OfflineList ?? new OfflineListResult(false, true, Array.Empty<OfflineListItem>());
        var verdict = OfflineScanEvaluator.Evaluate(_session.Verifier, signedQr, list, now);

        // TOUT scan hors-ligne rattaché à un QR connu est PERSISTÉ pour
        // resynchronisation — accordé comme refusé (REQ-F-07 : journaliser chaque
        // tentative ; §6.2 : chaque validation dégradée marquée dans le journal).
        // Il doit survivre à un redémarrage pendant la coupure.
        if (verdict.VisitToken is { } token)
            await _session.EnqueueOfflineScanAsync(new OfflineScanDto(
                token, _session.Direction, verdict.Outcome == OfflineOutcome.Recognized, now,
                verdict.Outcome.ToString(), verdict.IsSecurityEvent));

        return ScanVerdict.FromOffline(verdict);
    }
}

/// <summary>Verdict affiché plein écran (§11) : couleur + titre + sous-titre.</summary>
public sealed record ScanVerdict(string Title, string Subtitle, string ColorHex, bool IsSecurityEvent)
{
    // Vert = autorisé, Bleu = sortie, Rouge = refus/sécurité.
    private const string Green = "#1E7E45";
    private const string Blue = "#1B6EC2";
    private const string Red = "#C0392B";
    private const string Orange = "#E67E22";

    public static ScanVerdict FromApi(string verdict, bool granted, bool checkOut, bool security, string? name) => verdict switch
    {
        "CHECKED_OUT" => new("SORTIE ENREGISTRÉE", name ?? "", Blue, false),
        "GRANTED" => new("ACCÈS AUTORISÉ", name ?? "", Green, false),
        "INVALID_SIGNATURE" => new("SIGNATURE INVALIDE", "QR altéré ou expiré", Red, true),
        _ when security => new("ACCÈS REFUSÉ", "Événement de sécurité", Red, true),
        _ => new("ACCÈS REFUSÉ", "Voir poste de garde", Orange, false),
    };

    public static ScanVerdict FromOffline(OfflineVerdict v) => v.Outcome switch
    {
        OfflineOutcome.Recognized => new("QR RECONNU (hors ligne)", "Appliquer entrée/sortie", Green, false),
        OfflineOutcome.InvalidSignature => new("SIGNATURE INVALIDE", "QR altéré", Red, true),
        OfflineOutcome.Excluded => new("ACCÈS REFUSÉ", "Voir poste de garde", Red, true),
        OfflineOutcome.TooEarly or OfflineOutcome.TooLate or OfflineOutcome.NonBusinessDay or OfflineOutcome.Expired
            => new("ACCÈS REFUSÉ", v.Message, Red, true),
        OfflineOutcome.NotInLocalList => new("VÉRIFICATION IMPOSSIBLE", "Hors ligne — QR inconnu localement", Orange, false),
        _ => new("VALIDATION IMPOSSIBLE", "Liste locale expirée", Orange, false),
    };
}
