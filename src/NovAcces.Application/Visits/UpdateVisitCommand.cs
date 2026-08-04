using Microsoft.Extensions.Logging;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Enums;
using NovAcces.Domain.Exceptions;

namespace NovAcces.Application.Visits;

public sealed record UpdateVisitCommand(
    Guid VisitId,
    string VisitorName,
    string VisitorCompany,
    string Motif,
    string? VisitorPhone,
    string? VisitorEmail,
    string RequestedByHostId);

public sealed record UpdateVisitResult(bool Success, string? Error, bool Forbidden = false, bool InvitationResent = false);

/// <summary>
/// Correction des coordonnées d'un visiteur AVANT son arrivée — cas d'usage :
/// erreur de saisie à la création (REQ-F-03, retour client). Contrairement à
/// la révocation, réservée aux propres demandes de l'hôte : la sûreté n'a pas
/// de raison de retoucher l'identité d'un visiteur qu'elle n'a pas invité.
/// </summary>
public sealed class UpdateVisitHandler
{
    private readonly IVisitRepository _visits;
    private readonly IQrSigningService _signing;
    private readonly IManualCodeService _manualCode;
    private readonly INotificationService _notifications;
    private readonly IAdminAuditLog _audit;
    private readonly ILogger<UpdateVisitHandler> _logger;

    public UpdateVisitHandler(
        IVisitRepository visits,
        IQrSigningService signing,
        IManualCodeService manualCode,
        INotificationService notifications,
        IAdminAuditLog audit,
        ILogger<UpdateVisitHandler> logger)
    {
        _visits = visits;
        _signing = signing;
        _manualCode = manualCode;
        _notifications = notifications;
        _audit = audit;
        _logger = logger;
    }

    public async Task<UpdateVisitResult> HandleAsync(UpdateVisitCommand command, CancellationToken ct)
    {
        var visit = await _visits.GetByIdAsync(command.VisitId, ct);
        if (visit is null)
            return new UpdateVisitResult(false, "Demande introuvable.");

        if (visit.HostUserId != command.RequestedByHostId)
            return new UpdateVisitResult(false, "Vous ne pouvez modifier que vos propres demandes.", Forbidden: true);

        // Anti-doublon (même règle qu'à la création) : une correction de nom/
        // société ne doit pas entrer en collision avec une autre demande active.
        var identityChanged =
            !string.Equals(visit.VisitorName, command.VisitorName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(visit.VisitorCompany, command.VisitorCompany, StringComparison.OrdinalIgnoreCase);
        if (identityChanged
            && await _visits.HasActiveVisitForVisitorAsync(command.VisitorName, command.VisitorCompany, ct))
            return new UpdateVisitResult(false, "Une demande active existe déjà pour ce visiteur.");

        var emailChanged = !string.Equals(visit.VisitorEmail, command.VisitorEmail, StringComparison.OrdinalIgnoreCase);

        try
        {
            visit.UpdateVisitorDetails(
                command.VisitorName, command.VisitorCompany, command.Motif,
                command.VisitorPhone, command.VisitorEmail);
        }
        catch (DomainException ex)
        {
            return new UpdateVisitResult(false, ex.Message);
        }

        // L'email a changé : le message déjà envoyé (s'il y en a eu un) est
        // parti à la mauvaise adresse. Le renvoi émet un NOUVEAU code de
        // secours (l'ancien, non recouvrable depuis son empreinte, devient
        // silencieusement invalide — même limite que TerminalDirectory pour
        // une clé API) : on le génère avant la sauvegarde pour qu'un seul
        // SaveChangesAsync couvre coordonnées + code.
        var willResend = emailChanged && !string.IsNullOrWhiteSpace(visit.VisitorEmail);
        string? rawCode = null;
        if (willResend)
        {
            var (newRawCode, newCodeHash) = _manualCode.GenerateCode();
            visit.AssignManualCode(newCodeHash);
            rawCode = newRawCode;
        }

        await _visits.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            AdminAuditAction.VisitUpdated, command.RequestedByHostId, command.VisitId.ToString(),
            $"Coordonnées corrigées pour la visite {command.VisitId}.", ct);

        var invitationResent = false;
        if (willResend)
        {
            try
            {
                var expiresAt = visit.ComputeQrExpiry();
                var signedPayload = _signing.SignVisitToken(visit.Id, visit.VisitToken, expiresAt);
                await _notifications.SendVisitInvitationAsync(
                    new VisitInvitationNotification(
                        visit.Id, visit.VisitorName, visit.VisitorEmail, signedPayload, visit.ScheduledAt, expiresAt, rawCode!),
                    ct);
                invitationResent = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Échec du renvoi du QR après correction d'email pour la visite {VisitId}.", command.VisitId);
            }
        }

        return new UpdateVisitResult(true, null, InvitationResent: invitationResent);
    }
}
