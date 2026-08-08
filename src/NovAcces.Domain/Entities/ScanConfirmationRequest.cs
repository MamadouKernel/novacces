using NovAcces.Domain.Enums;
using NovAcces.Domain.Exceptions;

namespace NovAcces.Domain.Entities;

/// <summary>
/// Demande de validation SANS QR ni code de secours, déclenchée par l'agent
/// depuis la liste « Attendus » de l'app mobile (tap sur un visiteur). Ne
/// donne PAS accès directement — l'agent obtenait auparavant systématiquement
/// un refus (INVALID_SIGNATURE, un visitId brut n'est pas un QR signé valide),
/// désormais un vrai flux : la sûreté du site est notifiée (SignalR + push +
/// email) et doit confirmer explicitement depuis le portail Web AVANT que
/// l'accès ne soit réellement accordé.
///
/// Cette entité ne fait QUE porter la décision humaine (Pending/Approved/
/// Denied/Expired) — l'octroi effectif de l'accès (anti-rejeu, liste
/// d'exclusion relue en direct, fenêtre de validité, cycle entrée/sortie)
/// reste intégralement délégué à ScanExecutionCore au moment de l'approbation,
/// jamais dupliqué ici (même principe que ScanQrHandler/ScanManualCodeHandler).
/// Un « Approved » ici ne garantit donc PAS un accès accordé : la revérification
/// au moment de l'approbation peut encore refuser (ex. exclusion ajoutée entre
/// temps) — c'est le comportement voulu, pas un bug.
/// </summary>
public class ScanConfirmationRequest
{
    public Guid Id { get; private set; }
    public Guid VisitId { get; private set; }

    /// <summary>Instantané au moment de la demande (comme ScanLogEntry.VisitorName) — affichage sûreté sans jointure.</summary>
    public string VisitorName { get; private set; } = default!;

    public CheckpointDirection Direction { get; private set; }
    public string? CheckpointId { get; private set; }

    /// <summary>Matricule de l'agent demandeur — même identifiant que celui journalisé par un scan normal.</summary>
    public string AgentId { get; private set; } = default!;

    /// <summary>Terminal demandeur — cible la notification push de résolution (pas tous les terminaux du site).</summary>
    public Guid RequestingTerminalId { get; private set; }

    public ConfirmationRequestStatus Status { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    public string? DecidedBy { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }

    private ScanConfirmationRequest() { } // EF Core

    public static ScanConfirmationRequest Create(
        Guid visitId, string visitorName, CheckpointDirection direction, string? checkpointId,
        string agentId, Guid requestingTerminalId, DateTimeOffset now, TimeSpan ttl)
    {
        if (visitId == Guid.Empty)
            throw new DomainException("Visite invalide.");
        if (string.IsNullOrWhiteSpace(agentId))
            throw new DomainException("Agent demandeur requis.");
        if (ttl <= TimeSpan.Zero)
            throw new DomainException("Le délai de confirmation doit être positif.");

        return new ScanConfirmationRequest
        {
            Id = Guid.NewGuid(),
            VisitId = visitId,
            VisitorName = visitorName,
            Direction = direction,
            CheckpointId = checkpointId,
            AgentId = agentId,
            RequestingTerminalId = requestingTerminalId,
            Status = ConfirmationRequestStatus.Pending,
            RequestedAt = now,
            ExpiresAt = now.Add(ttl),
        };
    }

    public bool IsPending(DateTimeOffset now) => Status == ConfirmationRequestStatus.Pending && now < ExpiresAt;

    public void Approve(string decidedBy, DateTimeOffset now)
    {
        if (Status != ConfirmationRequestStatus.Pending)
            throw new DomainException("Cette demande a déjà été traitée.");
        if (now >= ExpiresAt)
            throw new DomainException("Cette demande a expiré — elle ne peut plus être confirmée.");

        Status = ConfirmationRequestStatus.Approved;
        DecidedBy = decidedBy;
        DecidedAt = now;
    }

    public void Deny(string decidedBy, DateTimeOffset now)
    {
        if (Status != ConfirmationRequestStatus.Pending)
            throw new DomainException("Cette demande a déjà été traitée.");

        Status = ConfirmationRequestStatus.Denied;
        DecidedBy = decidedBy;
        DecidedAt = now;
    }

    /// <summary>
    /// Refus implicite par expiration (décision du 08/08/2026 : sans réponse
    /// de la sûreté sous le délai, la demande n'accorde jamais l'accès).
    /// Retourne vrai si un changement a eu lieu, pour ne notifier qu'une fois.
    /// </summary>
    public bool ExpireIfPastDeadline(DateTimeOffset now)
    {
        if (Status != ConfirmationRequestStatus.Pending || now < ExpiresAt)
            return false;

        Status = ConfirmationRequestStatus.Expired;
        return true;
    }
}
