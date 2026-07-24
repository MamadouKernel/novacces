using NovAcces.Application.Abstractions;

namespace NovAcces.Application.Visits;

/// <param name="RequestedByHostId">Identité de l'appelant (pour le moindre privilège).</param>
/// <param name="CanRevokeAny">Vrai pour Sûreté/Admin ; un Hôte ne peut révoquer que ses propres demandes.</param>
public sealed record RevokeVisitCommand(
    Guid VisitId, string RevokedBy, string RequestedByHostId, bool CanRevokeAny);

public sealed record RevokeVisitResult(bool Success, string? Error, bool Forbidden = false);

/// <summary>
/// Révocation manuelle d'un QR par l'hôte ou la sûreté, à tout moment
/// (REQ-F-09 du CDC). Possible même si le visiteur est actuellement sur
/// site : la révocation empêche toute RÉ-entrée, mais ne bloque jamais
/// une sortie déjà en cours (cf. Visit.Scan — principe de sûreté validé
/// lors de la démonstration du 22/07/2026).
/// </summary>
public sealed class RevokeVisitHandler
{
    private readonly IVisitRepository _visits;

    public RevokeVisitHandler(IVisitRepository visits) => _visits = visits;

    public async Task<RevokeVisitResult> HandleAsync(RevokeVisitCommand command, CancellationToken ct)
    {
        var visit = await _visits.GetByIdAsync(command.VisitId, ct);
        if (visit is null)
            return new RevokeVisitResult(false, "Visite introuvable.");

        // Moindre privilège (section 8.5 du CDC) : un Hôte ne révoque QUE ses
        // propres demandes ; Sûreté/Admin peuvent révoquer tout QR du site.
        if (!command.CanRevokeAny && visit.HostUserId != command.RequestedByHostId)
            return new RevokeVisitResult(false, "Vous ne pouvez révoquer que vos propres demandes.", Forbidden: true);

        visit.Revoke();
        await _visits.SaveChangesAsync(ct);

        // Jalon 2 : journaliser explicitement l'action de révocation elle-même
        // (qui a révoqué, quand) dans un journal d'audit des actions
        // d'administration distinct du journal des scans (section 8.5 du CDC).

        return new RevokeVisitResult(true, null);
    }
}
