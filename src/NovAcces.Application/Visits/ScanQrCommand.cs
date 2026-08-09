using Microsoft.Extensions.Logging;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;

namespace NovAcces.Application.Visits;

public sealed record ScanQrCommand(
    string SignedQrPayload,
    CheckpointDirection Direction,
    string AgentId,
    bool IsDegradedMode,
    bool IsBusinessDayOverride, // fourni par l'appelant (calculé serveur ou depuis la liste hors ligne)
    string? CheckpointId = null
);

public sealed record ScanQrResult(
    bool IsGranted,
    bool IsCheckOut,
    bool IsSecurityEvent,
    string VerdictCode,   // "GRANTED" | "CHECKED_OUT" | "DENIED_xxx" | "INVALID_SIGNATURE" | "INVALID_CODE"
    string? VisitorName,
    int? OverstayMinutes,
    int? PresenceMinutes = null,   // durée de présence à la sortie (§1.6)
    // Visite résolue par ScanExecutionCore (null si introuvable) — permet à
    // l'appelant (ex. /api/scan/sync) de rapporter un conflit sans dépendre
    // d'une vérification indépendante côté QR (impossible pour un code de
    // secours, qui n'a pas d'équivalent hors-base à la signature ES256).
    Guid? VisitId = null
);

/// <summary>
/// Cas d'usage transactionnel du scan PAR QR. Vérifie la signature ES256
/// (spécifique au QR — la seule chose qui distingue ce chemin d'un scan par
/// code de secours), puis délègue tout le reste — chargement AVEC VERROU,
/// application de la règle, journalisation, diffusion, notification hôte —
/// à ScanExecutionCore, PARTAGÉ avec ScanManualCodeHandler pour que l'anti-
/// rejeu (REQ-SEC-03) n'existe qu'à un seul endroit. C'est l'unique point
/// d'entrée du scan QR, appelé identiquement par l'API (mode nominal) et
/// par le service de resynchronisation (scans réalisés hors ligne, cf.
/// REQ-SEC-06.d).
/// </summary>
public sealed class ScanQrHandler
{
    private readonly IQrSigningService _signing;
    private readonly IVisitRepository _visits;
    private readonly IScanLogRepository _logs;
    private readonly IDateTimeProvider _clock;
    private readonly ScanExecutionCore _core;

    public ScanQrHandler(
        IQrSigningService signing,
        IVisitRepository visits,
        IScanLogRepository logs,
        IDateTimeProvider clock,
        IScanEventBroadcaster broadcaster,
        IExclusionListService exclusionList,
        IHostDirectory hosts,
        INotificationService notifications,
        IUnitOfWork unitOfWork,
        ILogger<ScanQrHandler> logger)
    {
        _signing = signing;
        _visits = visits;
        _logs = logs;
        _clock = clock;
        _core = new ScanExecutionCore(
            visits, logs, broadcaster, exclusionList, hosts, notifications, unitOfWork, clock, logger);
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

        // 2-5. Anti-rejeu, journalisation, diffusion, notification hôte —
        // logique PARTAGÉE, voir ScanExecutionCore. Jamais dupliquée ici.
        return await _core.ExecuteAsync(
            token => _visits.GetForUpdateAsync(verification.VisitToken.Value, token),
            ScanAuthMethod.Qr, ScanDenialReason.InvalidSignature, "INVALID_SIGNATURE",
            "QR inconnu", "Signature cryptographique invalide — contenu du QR altéré ou forgé",
            command.Direction, command.AgentId, command.IsDegradedMode, command.IsBusinessDayOverride,
            command.CheckpointId, now, ct);
    }

    private async Task LogInvalidSignatureAsync(ScanQrCommand command, DateTimeOffset now, CancellationToken ct)
    {
        var entry = ScanLogEntry.Create(
            Guid.Empty, "QR inconnu", command.AgentId, command.Direction,
            ScanOutcome.Denied(ScanDenialReason.InvalidSignature, isSecurityEvent: true),
            command.IsDegradedMode,
            "Signature cryptographique invalide — contenu du QR altéré ou forgé",
            now, ScanAuthMethod.Qr);
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
            now, ScanAuthMethod.Qr);
        await _logs.AddAsync(entry, ct);
        await _logs.SaveChangesAsync(ct);
    }
}
