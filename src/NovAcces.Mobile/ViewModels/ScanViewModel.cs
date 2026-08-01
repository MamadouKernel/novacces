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
                var r = await _api.ScanAsync(signedQr, _session.Direction, _session.ShiftToken, ct);
                if (r is not null)
                    return ScanVerdict.FromApi(
                        r.VerdictCode, r.IsGranted, r.IsCheckOut, r.IsSecurityEvent, r.VisitorName,
                        r.PresenceMinutes, r.OverstayMinutes);
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

        // État LOCAL « sur site » (instantané serveur + scans locaux) : base de
        // l'anti-rejeu et du cycle directionnel hors-ligne.
        var onSite = await _session.ComputeOnSiteAsync();
        var verdict = OfflineScanEvaluator.Evaluate(
            _session.Verifier, signedQr, list, _session.Direction, onSite, now);

        // TOUT scan hors-ligne rattaché à un QR connu est PERSISTÉ pour
        // resynchronisation — accordé comme refusé (REQ-F-07 : journaliser chaque
        // tentative ; §6.2 : chaque validation dégradée marquée dans le journal).
        // Il doit survivre à un redémarrage pendant la coupure, et alimente aussi
        // le calcul de l'état « sur site » (entrée accordée / sortie effective).
        if (verdict.VisitToken is { } token)
        {
            var granted = verdict.Outcome is OfflineOutcome.Recognized or OfflineOutcome.CheckedOut;

            // L'enveloppe signée d'origine est conservée telle quelle : c'est
            // elle que le serveur rejoue à la resynchronisation (le verdict
            // local n'est qu'une indication, confrontée au registre central).
            await _session.EnqueueOfflineScanAsync(new OfflineScanDto(
                token, _session.Direction, granted, now,
                verdict.Outcome.ToString(), verdict.IsSecurityEvent, signedQr));
        }

        return ScanVerdict.FromOffline(verdict);
    }
}

/// <summary>Verdict affiché plein écran (§11) : couleur + titre + sous-titre.</summary>
public sealed record ScanVerdict(string Title, string Subtitle, string ColorHex, bool IsSecurityEvent)
{
    // Couleurs de verdict alignées sur la maquette (maquette_novacces.html) :
    // vert = autorisé, bleu = sortie, rouge = refus/sécurité, ambre = indéterminé.
    private const string Green = "#26C266";
    private const string Blue = "#1B5E9E";
    private const string Red = "#C92A2A";
    private const string Orange = "#E0932A";

    public static ScanVerdict FromApi(
        string verdict, bool granted, bool checkOut, bool security, string? name,
        int? presenceMinutes = null, int? overstayMinutes = null) => verdict switch
    {
        // §1.6 : la durée de présence est affichée à la sortie — c'est
        // l'information que l'agent lit à voix haute au visiteur.
        "CHECKED_OUT" => new("SORTIE ENREGISTRÉE", FormatSortie(name, presenceMinutes, overstayMinutes), Blue, false),
        "GRANTED" => new("ACCÈS AUTORISÉ", name ?? "", Green, false),
        "INVALID_SIGNATURE" => new("SIGNATURE INVALIDE", "QR altéré ou expiré", Red, true),
        _ when security => new("ACCÈS REFUSÉ", "Événement de sécurité", Red, true),
        _ => new("ACCÈS REFUSÉ", "Voir poste de garde", Orange, false),
    };

    private static string FormatSortie(string? name, int? presenceMinutes, int? overstayMinutes)
    {
        var nom = name ?? "";
        if (presenceMinutes is not { } minutes || minutes <= 0)
            return nom;

        var duree = minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60:00}";
        var depassement = overstayMinutes is > 0 ? $" · +{overstayMinutes} min" : "";
        return $"{nom} · {duree} sur site{depassement}";
    }

    public static ScanVerdict FromOffline(OfflineVerdict v) => v.Outcome switch
    {
        OfflineOutcome.Recognized => new("ACCÈS AUTORISÉ", "Hors ligne", Green, false),
        OfflineOutcome.CheckedOut => new("SORTIE ENREGISTRÉE", "Hors ligne", Blue, false),
        OfflineOutcome.SuspectedDuplicate => new("ACCÈS REFUSÉ", "Déjà sur site — suspicion de copie", Red, true),
        OfflineOutcome.NoActiveEntry => new("SORTIE IMPOSSIBLE", "Aucune entrée active", Orange, false),
        OfflineOutcome.InvalidSignature => new("SIGNATURE INVALIDE", "QR altéré", Red, true),
        OfflineOutcome.Excluded => new("ACCÈS REFUSÉ", "Voir poste de garde", Red, true),
        OfflineOutcome.TooEarly or OfflineOutcome.TooLate or OfflineOutcome.NonBusinessDay or OfflineOutcome.Expired
            => new("ACCÈS REFUSÉ", v.Message, Red, true),
        OfflineOutcome.NotInLocalList => new("VÉRIFICATION IMPOSSIBLE", "Hors ligne — QR inconnu localement", Orange, false),
        _ => new("VALIDATION IMPOSSIBLE", "Liste locale expirée", Orange, false),
    };
}
