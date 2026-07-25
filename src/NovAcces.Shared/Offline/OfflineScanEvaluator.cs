namespace NovAcces.Shared.Offline;

/// <summary>
/// Décision de scan en MODE DÉGRADÉ (hors ligne), côté agent. Pur, sans état ni
/// dépendance : le MAUI l'appelle avec le QR scanné, la liste locale signée du
/// jour, le SENS du poste (Entrée/Sortie) et l'ensemble des visiteurs
/// considérés « sur site » localement (cf. <see cref="OfflineOnSiteState"/>).
/// Reproduit la doctrine §6 des scénarios ET, en miroir de Domain/Visit.Scan,
/// le cycle directionnel entrée/sortie + l'anti-rejeu local.
///
/// L'ordre des contrôles suit exactement Domain/Visit.Scan : exclusion (sauf
/// déjà sur site), poste Sortie (jamais bloqué pour qui est sur site), poste
/// Entrée avec détection « déjà sur site » (suspicion de copie volée), puis
/// fenêtre/jour ouvré à l'entrée. La sync avec le registre central à la
/// reconnexion (resync) tranche les conflits éventuels.
/// </summary>
public static class OfflineScanEvaluator
{
    // Doivent correspondre à la fenêtre serveur (Domain/Visit). Répliquées ici
    // car la vérification hors-ligne est, par nature, côté client.
    public const int WindowBeforeMinutes = 20;
    public const int WindowAfterMinutes = 15;

    public static OfflineVerdict Evaluate(
        OfflineQrVerifier verifier, string signedQr, OfflineListResult list,
        string direction, IReadOnlySet<Guid> onSiteTokens, DateTimeOffset now)
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

        var onSite = onSiteTokens.Contains(item.VisitToken);
        var isExit = string.Equals(direction, "Exit", StringComparison.OrdinalIgnoreCase);

        OfflineVerdict Refuse(OfflineOutcome o, string m, bool secu) =>
            new(o, m, secu, item.VisitId, item.VisitToken);

        // 4. Liste d'exclusion : refus générique (motif jamais exposé). Une personne
        //    exclue mais DÉJÀ sur site peut néanmoins sortir (comme en ligne).
        if (item.IsExcluded && !onSite)
            return Refuse(OfflineOutcome.Excluded, "ACCÈS REFUSÉ — voir poste de garde", secu: true);

        // 5. Poste SORTIE : ne gère que des sorties. Une sortie n'est JAMAIS bloquée
        //    pour qui est sur site (CLAUDE.md §4) ; sinon, aucune entrée active.
        if (isExit)
        {
            return onSite
                ? Refuse(OfflineOutcome.CheckedOut, "SORTIE ENREGISTRÉE", secu: false)
                : Refuse(OfflineOutcome.NoActiveEntry, "AUCUNE ENTRÉE ACTIVE", secu: false);
        }

        // 6. Poste ENTRÉE, mais le visiteur est DÉJÀ sur site => suspicion de copie
        //    volée (ce n'est pas une sortie : chaque poste a un sens — CLAUDE.md §4).
        if (onSite)
            return Refuse(OfflineOutcome.SuspectedDuplicate, "ACCÈS REFUSÉ — déjà sur site", secu: true);

        // 7. Fenêtre de validité (ENTRÉE uniquement).
        if (item.ScheduledAt is { } scheduled)
        {
            // Mode Unique : fenêtre −20/+15 autour du rendez-vous.
            if (now < scheduled.AddMinutes(-WindowBeforeMinutes))
                return Refuse(OfflineOutcome.TooEarly,
                    $"TROP TÔT — fenêtre à {scheduled.AddMinutes(-WindowBeforeMinutes).LocalDateTime:HH:mm}", secu: true);

            if (now > scheduled.AddMinutes(WindowAfterMinutes))
                return Refuse(OfflineOutcome.TooLate, "HORS FENÊTRE DE VALIDITÉ", secu: true);
        }
        else
        {
            // Mode 30 jours : jours ouvrés uniquement (fériés indisponibles hors
            // ligne — vérifiés au retour en ligne lors de la resynchronisation).
            if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                return Refuse(OfflineOutcome.NonBusinessDay, "JOUR NON OUVRÉ", secu: true);
        }

        // 8. Entrée autorisée : l'app marque le visiteur « sur site » localement et
        //    met le scan en file pour resynchronisation.
        return Refuse(OfflineOutcome.Recognized, "ACCÈS AUTORISÉ (hors ligne)", secu: false);
    }
}

public enum OfflineOutcome
{
    Recognized,
    CheckedOut,
    NoActiveEntry,
    SuspectedDuplicate,
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
