namespace NovAcces.Shared.Dtos;

/// <summary>Demande de validation sans QR/code envoyée par l'agent depuis la liste « Attendus » (POST /api/agent/confirmation-requests).</summary>
public sealed record CreateConfirmationRequestDto(Guid VisitId, string Direction, string? CheckpointId = null);

/// <summary>
/// AlreadyPending distingue une nouvelle demande d'une demande déjà ouverte
/// retrouvée (double tap agent) — le client mobile affiche le même écran
/// d'attente dans les deux cas, mais peut journaliser la différence.
/// </summary>
public sealed record ConfirmationRequestCreatedDto(Guid RequestId, DateTimeOffset ExpiresAt, bool AlreadyPending);

/// <summary>Demande en attente listée au portail Sûreté (GET /api/dashboard/confirmation-requests).</summary>
public sealed record PendingConfirmationRequestDto(
    Guid Id, string VisitorName, string Direction, string? CheckpointId,
    string AgentId, DateTimeOffset RequestedAt, DateTimeOffset ExpiresAt);

/// <summary>Diffusion temps réel (SignalR, message « ConfirmationRequested ») d'une nouvelle demande au dashboard Sûreté du site.</summary>
public sealed record ConfirmationRequestedDto(
    Guid RequestId, string VisitorName, string? CheckpointId, string Direction, DateTimeOffset ExpiresAt);

/// <summary>
/// Réponse à l'approbation d'une demande — distincte du statut de la DEMANDE
/// elle-même : la sûreté peut approuver (Approved) alors que le scan réel est
/// malgré tout refusé par ScanExecutionCore (ex. exclusion ajoutée entre
/// temps), c'est le comportement voulu. VerdictCode reprend exactement les
/// mêmes valeurs qu'un scan QR/code normal.
/// </summary>
public sealed record ConfirmationRequestDecisionDto(
    bool IsGranted, bool IsCheckOut, bool IsSecurityEvent, string VerdictCode, string? VisitorName);
