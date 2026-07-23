using NovAcces.Application.Abstractions;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;

namespace NovAcces.Application.Visits;

public sealed record ScanQrCommand(
    string SignedQrPayload,
    CheckpointDirection Direction,
    string AgentId,
    bool IsDegradedMode,
    bool IsBusinessDayOverride // fourni par l'appelant (calculé serveur ou depuis la liste hors ligne)
);

public sealed record ScanQrResult(
    bool IsGranted,
    bool IsCheckOut,
    bool IsSecurityEvent,
    string VerdictCode,   // "GRANTED" | "CHECKED_OUT" | "DENIED_xxx" | "INVALID_SIGNATURE"
    string? VisitorName,
    int? OverstayMinutes
);

/// <summary>
/// Cas d'usage transactionnel du scan. Toute la logique métier est déléguée
/// à Visit.Scan() (Domain) — cette classe orchestre : vérification de
/// signature, chargement AVEC VERROU, application de la règle, journalisation,
/// sauvegarde. C'est l'unique point d'entrée du scan, appelé identiquement
/// par l'API (mode nominal) et par le service de resynchronisation
/// (scans réalisés hors ligne, cf. REQ-SEC-06.d).
/// </summary>
public sealed class ScanQrHandler
{
    private readonly IQrSigningService _signing;
    private readonly IVisitRepository _visits;
    private readonly IScanLogRepository _logs;
    private readonly IDateTimeProvider _clock;

    public ScanQrHandler(
        IQrSigningService signing,
        IVisitRepository visits,
        IScanLogRepository logs,
        IDateTimeProvider clock)
    {
        _signing = signing;
        _visits = visits;
        _logs = logs;
        _clock = clock;
    }

    public async Task<ScanQrResult> HandleAsync(ScanQrCommand command, CancellationToken ct)
    {
        var now = _clock.UtcNow;

        // 1. Vérification cryptographique — indépendante de la base, fonctionne hors ligne.
        var verification = _signing.VerifySignedToken(command.SignedQrPayload);
        if (!verification.IsValid || verification.VisitToken is null)
        {
            await LogInvalidSignatureAsync(command, now, ct);
            return new ScanQrResult(false, false, true, "INVALID_SIGNATURE", null, null);
        }

        // 1bis. Expiration cryptographique du jeton (REQ-SEC-04 : "expiration
        // intégrée"). C'est un filet de sécurité indépendant de la fenêtre
        // métier (-20/+15) : même si la logique métier changeait un jour,
        // un jeton dont l'expiration cryptographique est dépassée ne doit
        // JAMAIS être accepté.
        if (verification.ExpiresAt is not null && now > verification.ExpiresAt.Value)
        {
            await LogExpiredTokenAsync(command, now, ct);
            return new ScanQrResult(false, false, true, "INVALID_SIGNATURE", null, null);
        }

        // 2. Chargement AVEC VERROU PESSIMISTE — c'est ici que l'anti-rejeu se
        //    joue réellement en cas de scans concurrents (REQ-SEC-03).
        var visit = await _visits.GetForUpdateAsync(verification.VisitToken.Value, ct);
        if (visit is null)
        {
            await LogInvalidSignatureAsync(command, now, ct);
            return new ScanQrResult(false, false, true, "INVALID_SIGNATURE", null, null);
        }

        // 3. Application de la règle métier (Domain) — jamais dupliquée ici.
        var outcome = visit.Scan(command.Direction, command.IsBusinessDayOverride, now);

        // 4. Journalisation inaltérable, y compris pour les refus.
        var logEntry = ScanLogEntry.Create(
            visit.Id, visit.VisitorName, command.AgentId, command.Direction,
            outcome, command.IsDegradedMode, BuildDetail(outcome), now);

        await _logs.AddAsync(logEntry, ct);
        await _visits.SaveChangesAsync(ct);
        await _logs.SaveChangesAsync(ct);

        var verdict = outcome switch
        {
            { IsGranted: true, IsCheckOut: true } => "CHECKED_OUT",
            { IsGranted: true } => "GRANTED",
            _ => $"DENIED_{outcome.DenialReason}"
        };

        return new ScanQrResult(
            outcome.IsGranted, outcome.IsCheckOut, outcome.IsSecurityEvent,
            verdict, visit.VisitorName,
            outcome.IsCheckOut ? outcome.OverstayMinutesAtCheckOut : null);
    }

    private async Task LogInvalidSignatureAsync(ScanQrCommand command, DateTimeOffset now, CancellationToken ct)
    {
        var entry = ScanLogEntry.Create(
            Guid.Empty, "QR inconnu", command.AgentId, command.Direction,
            ScanOutcome.Denied(ScanDenialReason.InvalidSignature, isSecurityEvent: true),
            command.IsDegradedMode,
            "Signature cryptographique invalide — contenu du QR altéré ou forgé",
            now);
        await _logs.AddAsync(entry, ct);
        await _logs.SaveChangesAsync(ct);
    }

    private async Task LogExpiredTokenAsync(ScanQrCommand command, DateTimeOffset now, CancellationToken ct)
    {
        var entry = ScanLogEntry.Create(
            Guid.Empty, "QR expiré", command.AgentId, command.Direction,
            ScanOutcome.Denied(ScanDenialReason.InvalidSignature, isSecurityEvent: true),
            command.IsDegradedMode,
            "Jeton cryptographique expiré (REQ-SEC-04) — expiration dépassée",
            now);
        await _logs.AddAsync(entry, ct);
        await _logs.SaveChangesAsync(ct);
    }

    private static string BuildDetail(ScanOutcome outcome) => outcome switch
    {
        { IsCheckOut: true, OverstayMinutesAtCheckOut: > 0 } o =>
            $"Sortie enregistrée · dépassement de la durée prévue : +{o.OverstayMinutesAtCheckOut} min",
        { IsCheckOut: true } => "Sortie enregistrée",
        { IsGranted: true } => "Accès autorisé",
        { DenialReason: ScanDenialReason.Excluded } => "Personne figurant sur la liste d'exclusion du site (REQ-F-11)",
        { DenialReason: ScanDenialReason.SuspectedDuplicate } => "QR présenté à l'entrée alors qu'une entrée est déjà enregistrée — suspicion de copie",
        { DenialReason: ScanDenialReason.CycleAlreadyClosed } => "Réutilisation d'un QR unique après sortie du visiteur",
        { DenialReason: ScanDenialReason.AlreadyConsumed } => "Tentative de réutilisation d'un QR à passage unique (anti-rejeu)",
        { DenialReason: ScanDenialReason.TooEarly } => "Présentation avant l'ouverture de la fenêtre de validité",
        { DenialReason: ScanDenialReason.TooLate } => "Tentative hors fenêtre de validité",
        { DenialReason: ScanDenialReason.NonBusinessDay } => "Accès 30 jours présenté un jour non ouvré",
        { DenialReason: ScanDenialReason.Revoked } => "Scan d'un QR révoqué",
        { DenialReason: ScanDenialReason.NoActiveEntry } => "Scan au poste sortie sans entrée enregistrée",
        _ => "Refus"
    };
}
