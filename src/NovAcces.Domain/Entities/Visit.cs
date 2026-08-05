using NovAcces.Domain.Enums;
using NovAcces.Domain.Exceptions;

namespace NovAcces.Domain.Entities;

/// <summary>
/// Une demande de visite et son cycle de vie complet.
/// Toute la logique de sécurité (fenêtre, anti-rejeu, cycle entrée/sortie) est
/// centralisée ici : jamais côté application mobile, jamais côté portail web
/// (REQ-SEC-02 du CDC).
/// </summary>
public class Visit
{
    public Guid Id { get; private set; }

    /// <summary>Identifiant de visite tel qu'il apparaît, chiffré et signé, dans le QR.</summary>
    public Guid VisitToken { get; private set; }

    public string VisitorName { get; private set; } = default!;
    public string VisitorCompany { get; private set; } = default!;
    public string? VisitorPhone { get; private set; }
    public string? VisitorEmail { get; private set; }
    public string Motif { get; private set; } = default!;

    public string HostUserId { get; private set; } = default!;

    public AccessMode Mode { get; private set; }
    public VisitStatus Status { get; private set; }

    /// <summary>Horodatage du rendez-vous prévu (mode Unique uniquement).</summary>
    public DateTimeOffset? ScheduledAt { get; private set; }

    /// <summary>Durée de visite prévue en minutes (supervision des dépassements).</summary>
    public int PlannedDurationMinutes { get; private set; }

    public bool IsOnSite { get; private set; }
    public DateTimeOffset? CheckedInAt { get; private set; }
    public DateTimeOffset? CheckedOutAt { get; private set; }

    /// <summary>Vrai dès qu'un cycle entrée/sortie complet a eu lieu (mode Unique => QR définitivement clos).</summary>
    public bool HasCompletedCycle { get; private set; }

    /// <summary>
    /// Vrai si la personne figurait sur la liste d'exclusion du site AU MOMENT
    /// DE LA CRÉATION de la demande (REQ-F-11). C'est un instantané historique,
    /// PAS la source de vérité du scan : une personne inscrite sur la liste
    /// après l'émission de son QR doit être refusée elle aussi. L'état courant
    /// est relu en base et passé à <see cref="Scan"/> — voir le paramètre
    /// <c>isOnExclusionList</c>.
    /// </summary>
    public bool IsExcluded { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    // Audit de la révocation (REQ-F-09, traçabilité §8.5) : qui et quand.
    public string? RevokedBy { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    // Alertes de dépassement (supervision, jamais bloquant)
    public int OverstayLevel { get; private set; }
    public DateTimeOffset? LastOverstayAlertAt { get; private set; }

    /// <summary>
    /// Empreinte du code de secours saisissable manuellement (alternative au
    /// QR — voir Scan()). Jamais le code en clair : comme pour une clé API
    /// de terminal, seule l'empreinte est persistée (voir IManualCodeService).
    /// </summary>
    public string? ManualCodeHash { get; private set; }

    private Visit() { } // EF Core

    public static Visit Create(
        string visitorName,
        string visitorCompany,
        string motif,
        string hostUserId,
        AccessMode mode,
        DateTimeOffset? scheduledAt,
        int plannedDurationMinutes,
        string? visitorPhone,
        string? visitorEmail,
        bool isExcluded,
        DateTimeOffset now)
    {
        if (mode == AccessMode.Unique && scheduledAt is null)
            throw new DomainException("Un rendez-vous doit être fourni pour un accès à passage unique.");

        return new Visit
        {
            Id = Guid.NewGuid(),
            VisitToken = Guid.NewGuid(),
            VisitorName = visitorName,
            VisitorCompany = visitorCompany,
            Motif = motif,
            HostUserId = hostUserId,
            Mode = mode,
            Status = VisitStatus.Valid,
            ScheduledAt = scheduledAt,
            PlannedDurationMinutes = plannedDurationMinutes <= 0 ? 60 : plannedDurationMinutes,
            VisitorPhone = visitorPhone,
            VisitorEmail = visitorEmail,
            IsExcluded = isExcluded,
            CreatedAt = now
        };
    }

    /// <summary>
    /// Fenêtre de validité calculée côté serveur exclusivement (REQ-SEC-02).
    /// </summary>
    public WindowState EvaluateWindow(DateTimeOffset now)
    {
        if (Mode != AccessMode.Unique || ScheduledAt is null)
            return WindowState.Ok;

        var opensAt = ScheduledAt.Value.AddMinutes(-20);
        var closesAt = ScheduledAt.Value.AddMinutes(15);

        if (now < opensAt) return WindowState.TooEarly;
        if (now > closesAt) return WindowState.TooLate;
        return WindowState.Ok;
    }

    /// <summary>
    /// Coeur de l'anti-rejeu et du cycle entrée/sortie directionnel.
    /// Appelé dans une transaction sérialisable avec verrou pessimiste (voir
    /// Infrastructure/Persistence — REQ-SEC-03 : consommation atomique).
    /// </summary>
    /// <param name="isOnExclusionList">
    /// État COURANT de la liste d'exclusion du site pour ce visiteur, relu en
    /// base au moment du scan par l'appelant. Paramètre obligatoire et non
    /// optionnel à dessein : un appelant qui oublierait de le fournir laisserait
    /// passer une personne écartée, c'est au compilateur de l'en empêcher.
    /// </param>
    public ScanOutcome Scan(
        CheckpointDirection direction, bool isBusinessDay, DateTimeOffset now, bool isOnExclusionList)
    {
        // 1. Liste d'exclusion : refus générique, motif réservé à la sûreté (REQ-F-11).
        //    On retient l'instantané de création OU l'état courant : une personne
        //    ajoutée à la liste APRÈS l'émission de son QR doit être refusée —
        //    c'est le cas d'usage principal (personne écartée en cours de route).
        //    Réciproquement, on ne « déverrouille » pas un QR émis pour une
        //    personne déjà exclue : son retrait de la liste est une décision de
        //    sûreté qui passe par une nouvelle demande de visite.
        if ((IsExcluded || isOnExclusionList) && !IsOnSite)
            return ScanOutcome.Denied(ScanDenialReason.Excluded, isSecurityEvent: true);

        // 2. Poste SORTIE : ne gère que des sorties, jamais d'entrée
        if (direction == CheckpointDirection.Exit)
        {
            if (!IsOnSite)
                return ScanOutcome.Denied(ScanDenialReason.NoActiveEntry, isSecurityEvent: false);

            return CheckOut(now);
        }

        // 3. Poste ENTRÉE : le titulaire est déjà sur site => suspicion de copie/vol
        if (IsOnSite)
            return ScanOutcome.Denied(ScanDenialReason.SuspectedDuplicate, isSecurityEvent: true);

        // 4. Statuts bloquants
        if (Status == VisitStatus.Revoked)
            return ScanOutcome.Denied(ScanDenialReason.Revoked, isSecurityEvent: true);

        if (Mode == AccessMode.Unique && HasCompletedCycle)
            return ScanOutcome.Denied(ScanDenialReason.CycleAlreadyClosed, isSecurityEvent: true);

        if (Mode == AccessMode.Unique && Status == VisitStatus.Consumed)
            return ScanOutcome.Denied(ScanDenialReason.AlreadyConsumed, isSecurityEvent: true);

        // 5. Fenêtre de validité (mode Unique)
        if (Mode == AccessMode.Unique)
        {
            var window = EvaluateWindow(now);
            if (window == WindowState.TooEarly)
                return ScanOutcome.Denied(ScanDenialReason.TooEarly, isSecurityEvent: true);
            if (window == WindowState.TooLate)
                return ScanOutcome.Denied(ScanDenialReason.TooLate, isSecurityEvent: true);
        }

        // 5bis. Mode 30 jours : la période de 30 jours calendaires depuis la
        // création ne doit jamais être dépassée (REQ-F-05 — un accès "30 jours"
        // n'est pas un accès permanent). Gap identifié et corrigé le 23/07/2026 :
        // absent de la première version du scaffold.
        if (Mode == AccessMode.ThirtyDays && now > CreatedAt.AddDays(30))
            return ScanOutcome.Denied(ScanDenialReason.TooLate, isSecurityEvent: true);

        // 6. Jours ouvrés (mode 30 jours) — REQ-F-05
        if (Mode == AccessMode.ThirtyDays && !isBusinessDay)
            return ScanOutcome.Denied(ScanDenialReason.NonBusinessDay, isSecurityEvent: true);

        // 7. Entrée autorisée — consommation atomique si mode Unique (REQ-SEC-03)
        IsOnSite = true;
        CheckedInAt = now;
        OverstayLevel = 0;
        LastOverstayAlertAt = null;

        if (Mode == AccessMode.Unique)
            Status = VisitStatus.Consumed;

        return ScanOutcome.Granted();
    }

    private ScanOutcome CheckOut(DateTimeOffset now)
    {
        var overstayMinutes = ComputeOverstayMinutes(now);
        var presenceMinutes = ComputePresenceMinutes(now);

        IsOnSite = false;
        CheckedOutAt = now;
        OverstayLevel = 0;
        LastOverstayAlertAt = null;

        if (Mode == AccessMode.Unique)
            HasCompletedCycle = true;

        return ScanOutcome.CheckedOut(overstayMinutes, presenceMinutes);
    }

    /// <summary>
    /// Sortie enregistrée par la SÛRETÉ depuis le dashboard, sans scan
    /// (téléphone déchargé, visiteur reparti par un autre accès, oubli). Même
    /// effet qu'une sortie au poste : le visiteur cesse d'être compté présent
    /// et ses alertes de dépassement retombent — sans quoi il resterait
    /// « présent » et en dépassement indéfiniment.
    ///
    /// Passe par le domaine et non par une écriture directe : la clôture d'un
    /// cycle est une règle de sûreté, elle n'a pas à être réécrite ailleurs.
    /// </summary>
    public ScanOutcome ForceCheckOut(DateTimeOffset now)
    {
        if (!IsOnSite)
            return ScanOutcome.Denied(ScanDenialReason.NoActiveEntry, isSecurityEvent: false);

        return CheckOut(now);
    }

    /// <summary>
    /// Expiration CRYPTOGRAPHIQUE du QR de cette visite. Définie ici, et non
    /// chez l'appelant, pour que la génération initiale et toute réémission
    /// ultérieure produisent rigoureusement le même jeton : deux formules
    /// divergentes donneraient deux QR d'expirations différentes pour une même
    /// visite, dont un que le poste refuserait sans explication.
    /// </summary>
    public DateTimeOffset ComputeQrExpiry() =>
        Mode == AccessMode.Unique && ScheduledAt is { } scheduled
            ? scheduled.AddMinutes(15)   // borne haute de la fenêtre -20/+15
            : CreatedAt.AddDays(30);     // accès 30 jours, depuis la création

    /// <summary>
    /// Statut D'AFFICHAGE uniquement (jamais persisté) : vrai si la fenêtre/
    /// période de validité est dépassée sans que la demande ait été
    /// consommée, révoquée, ou soit en cours de visite. <see cref="Status"/>
    /// ne change JAMAIS avec le seul écoulement du temps — par construction,
    /// aucune tâche de fond ne "fait passer" une visite à un état expiré, ce
    /// qui éviterait une classe entière de bugs de synchronisation. C'est
    /// exactement pourquoi Scan() ne s'appuie jamais sur cette méthode : il
    /// recalcule la fenêtre lui-même (EvaluateWindow) au moment du scan, seule
    /// source de vérité pour un contrôle d'accès. Cette méthode sert
    /// uniquement à ce qu'un écran de suivi (portail hôte) affiche "Expirée"
    /// plutôt que "Valide" pour une demande qui ne peut plus être présentée
    /// avec succès — bug identifié le 05/08/2026 : l'API renvoyait le statut
    /// brut (toujours "Valid") sans jamais recalculer l'expiration.
    /// </summary>
    public bool IsExpiredForDisplay(DateTimeOffset now) =>
        Status == VisitStatus.Valid && !IsOnSite && now > ComputeQrExpiry();

    /// <summary>Durée de présence effective, en minutes (0 si jamais entré).</summary>
    public int ComputePresenceMinutes(DateTimeOffset now)
    {
        if (CheckedInAt is null) return 0;
        var minutes = (now - CheckedInAt.Value).TotalMinutes;
        return minutes > 0 ? (int)minutes : 0;
    }

    /// <summary>
    /// Attribue (ou renouvelle) le code de secours saisissable manuellement.
    /// Contrairement au VisitToken, ce code PEUT être régénéré : il n'est pas
    /// récupérable depuis son empreinte (comme une clé API de terminal), donc
    /// un renvoi d'invitation après correction ne peut pas réafficher
    /// l'ancien — il en émet un nouveau, ce qui invalide silencieusement le
    /// précédent (voir UpdateVisitHandler).
    /// </summary>
    public void AssignManualCode(string manualCodeHash)
    {
        if (string.IsNullOrWhiteSpace(manualCodeHash))
            throw new DomainException("Le code de secours doit avoir une empreinte valide.");

        ManualCodeHash = manualCodeHash;
    }

    /// <summary>Révocation manuelle par l'hôte ou la sûreté (REQ-F-09), possible à tout moment.</summary>
    public void Revoke(string revokedBy, DateTimeOffset now)
    {
        Status = VisitStatus.Revoked;
        RevokedBy = revokedBy;
        RevokedAt = now;
    }

    /// <summary>
    /// Corrige les coordonnées d'un visiteur avant son arrivée (nom, société,
    /// motif, contacts) — cas d'usage : erreur de saisie à la création.
    /// Volontairement restreint à une demande VALID et pas encore arrivée
    /// (IsOnSite = false) : au-delà, ce serait changer l'identité d'une
    /// personne en cours de visite, ce qui doit passer par une révocation +
    /// nouvelle demande, jamais une correction silencieuse. Ne touche ni le
    /// VisitToken ni les dates : le QR déjà émis reste valable tel quel.
    /// </summary>
    public void UpdateVisitorDetails(
        string visitorName, string visitorCompany, string motif, string? visitorPhone, string? visitorEmail)
    {
        if (Status != VisitStatus.Valid || IsOnSite)
            throw new DomainException("Seule une demande valide et pas encore arrivée peut être modifiée.");

        VisitorName = visitorName;
        VisitorCompany = visitorCompany;
        Motif = motif;
        VisitorPhone = visitorPhone;
        VisitorEmail = visitorEmail;
    }

    /// <summary>Dépassement de la durée de visite prévue — supervision, jamais bloquant.</summary>
    public int ComputeOverstayMinutes(DateTimeOffset now)
    {
        if (!IsOnSite || CheckedInAt is null) return 0;
        var expectedCheckOut = CheckedInAt.Value.AddMinutes(PlannedDurationMinutes);
        var overstay = (now - expectedCheckOut).TotalMinutes;
        return overstay > 0 ? (int)overstay : 0;
    }

    /// <summary>
    /// Retourne le niveau d'alerte à déclencher (0 = aucun), avec anti-spam
    /// (un rappel au maximum par intervalle configuré).
    /// </summary>
    public int EvaluateOverstayAlertLevel(DateTimeOffset now, TimeSpan reminderInterval)
    {
        var overstay = ComputeOverstayMinutes(now);
        if (overstay <= 0) return 0;

        if (LastOverstayAlertAt is null)
        {
            OverstayLevel = 1;
            LastOverstayAlertAt = now;
            return OverstayLevel;
        }

        if (now - LastOverstayAlertAt.Value >= reminderInterval)
        {
            OverstayLevel++;
            LastOverstayAlertAt = now;
            return OverstayLevel;
        }

        return 0; // pas encore l'heure du prochain rappel
    }
}

public enum WindowState { Ok, TooEarly, TooLate }

public enum ScanDenialReason
{
    Excluded,
    NoActiveEntry,
    SuspectedDuplicate,
    Revoked,
    CycleAlreadyClosed,
    AlreadyConsumed,
    TooEarly,
    TooLate,
    NonBusinessDay,
    InvalidSignature,

    /// <summary>Code de secours introuvable ou mal saisi — distinct d'InvalidSignature (jamais un QR forgé/altéré).</summary>
    InvalidManualCode
}

/// <summary>Résultat d'un scan — jamais d'exception pour un refus métier normal.</summary>
public sealed class ScanOutcome
{
    public bool IsGranted { get; }
    public bool IsCheckOut { get; }
    public bool IsSecurityEvent { get; }
    public ScanDenialReason? DenialReason { get; }
    public int OverstayMinutesAtCheckOut { get; }

    /// <summary>Durée de présence effective à la sortie, en minutes (§1.6 : affichée à l'agent).</summary>
    public int PresenceMinutesAtCheckOut { get; }

    private ScanOutcome(
        bool granted, bool checkOut, bool securityEvent, ScanDenialReason? reason, int overstay, int presence)
    {
        IsGranted = granted;
        IsCheckOut = checkOut;
        IsSecurityEvent = securityEvent;
        DenialReason = reason;
        OverstayMinutesAtCheckOut = overstay;
        PresenceMinutesAtCheckOut = presence;
    }

    public static ScanOutcome Granted() => new(true, false, false, null, 0, 0);
    public static ScanOutcome CheckedOut(int overstayMinutes, int presenceMinutes = 0) =>
        new(true, true, false, null, overstayMinutes, presenceMinutes);
    public static ScanOutcome Denied(ScanDenialReason reason, bool isSecurityEvent) =>
        new(false, false, isSecurityEvent, reason, 0, 0);
}
