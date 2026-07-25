namespace NovAcces.Shared.Offline;

/// <summary>
/// Décision de scan en MODE DÉGRADÉ (hors ligne), côté agent. Pur, sans état ni
/// dépendance : le MAUI l'appelle avec le QR scanné et la liste locale signée du
/// jour, puis affiche le verdict et gère l'enregistrement local (entrée/sortie,
/// resynchronisation). Reproduit la doctrine §6 des scénarios.
///
/// Limites assumées du hors-ligne : la fenêtre −20/+15 et le jour ouvré sont
/// vérifiés localement à partir de la liste signée ; l'anti-rejeu complet et
/// l'état « déjà sur site » relèvent de l'état LOCAL géré par le MAUI (SQLite),
/// confronté au registre central à la reconnexion (resync).
/// </summary>
public static class OfflineScanEvaluator
{
    // Doivent correspondre à la fenêtre serveur (Domain/Visit). Répliquées ici
    // car la vérification hors-ligne est, par nature, côté client.
    public const int WindowBeforeMinutes = 20;
    public const int WindowAfterMinutes = 15;

    public static OfflineVerdict Evaluate(
        OfflineQrVerifier verifier, string signedQr, OfflineListResult list, DateTimeOffset now)
    {
        // 1. Liste locale absente ou TTL dépassé : plus aucune validation possible.
        if (!list.IsValid || list.IsExpired)
            return new OfflineVerdict(OfflineOutcome.ListUnavailable,
                "VALIDATION IMPOSSIBLE — liste locale expirée", IsSecurityEvent: false);

        // 2. Signature du QR : vérification purement mathématique (clé publique).
        var token = verifier.VerifyToken(signedQr);
        if (!token.IsValid || token.VisitToken is null)
            return new OfflineVerdict(OfflineOutcome.InvalidSignature,
                "SIGNATURE INVALIDE — QR altéré", IsSecurityEvent: true);

        // 2bis. Expiration cryptographique intégrée au jeton (REQ-SEC-04) : vérifiée
        // AUSSI hors ligne. Indispensable pour le mode 30 jours, dont l'expiration
        // (création + 30 j) n'a AUCUN autre point de contrôle local — sans cela, un
        // QR 30 jours expiré serait « reconnu » hors ligne un jour ouvré.
        if (token.ExpiresAt is { } exp && now > exp)
            return new OfflineVerdict(OfflineOutcome.Expired,
                "QR EXPIRÉ", IsSecurityEvent: true, VisitToken: token.VisitToken);

        // 3. Le QR doit figurer dans la liste signée du jour.
        OfflineListItem? item = null;
        foreach (var e in list.Entries)
            if (e.VisitToken == token.VisitToken.Value) { item = e; break; }

        if (item is null)
            return new OfflineVerdict(OfflineOutcome.NotInLocalList,
                "VÉRIFICATION IMPOSSIBLE — hors ligne", IsSecurityEvent: false);

        // 4. Liste d'exclusion : refus générique (motif jamais exposé à l'agent).
        if (item.IsExcluded)
            return new OfflineVerdict(OfflineOutcome.Excluded,
                "ACCÈS REFUSÉ — voir poste de garde", IsSecurityEvent: true,
                VisitId: item.VisitId, VisitToken: item.VisitToken);

        // 5. Fenêtre de validité.
        if (item.ScheduledAt is { } scheduled)
        {
            // Mode Unique : fenêtre −20/+15 autour du rendez-vous.
            if (now < scheduled.AddMinutes(-WindowBeforeMinutes))
                return new OfflineVerdict(OfflineOutcome.TooEarly,
                    $"TROP TÔT — fenêtre à {scheduled.AddMinutes(-WindowBeforeMinutes).LocalDateTime:HH:mm}",
                    IsSecurityEvent: true, VisitId: item.VisitId, VisitToken: item.VisitToken);

            if (now > scheduled.AddMinutes(WindowAfterMinutes))
                return new OfflineVerdict(OfflineOutcome.TooLate,
                    "HORS FENÊTRE DE VALIDITÉ", IsSecurityEvent: true,
                    VisitId: item.VisitId, VisitToken: item.VisitToken);
        }
        else
        {
            // Mode 30 jours : jours ouvrés uniquement (fériés indisponibles hors
            // ligne — vérifiés au retour en ligne lors de la resynchronisation).
            if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                return new OfflineVerdict(OfflineOutcome.NonBusinessDay,
                    "JOUR NON OUVRÉ", IsSecurityEvent: true,
                    VisitId: item.VisitId, VisitToken: item.VisitToken);
        }

        // 6. QR reconnu et valide selon l'instantané local : l'app applique alors
        //    l'entrée/sortie selon son état local et marque le scan pour resync.
        return new OfflineVerdict(OfflineOutcome.Recognized,
            "QR reconnu — appliquer entrée/sortie (état local)", IsSecurityEvent: false,
            VisitId: item.VisitId, VisitToken: item.VisitToken);
    }
}

public enum OfflineOutcome
{
    Recognized,
    InvalidSignature,
    NotInLocalList,
    ListUnavailable,
    Excluded,
    TooEarly,
    TooLate,
    NonBusinessDay,
    Expired,
}

/// <summary>Verdict d'un scan hors-ligne : issue, message agent, et marqueurs.</summary>
public sealed record OfflineVerdict(
    OfflineOutcome Outcome,
    string Message,
    bool IsSecurityEvent,
    Guid? VisitId = null,
    Guid? VisitToken = null);
