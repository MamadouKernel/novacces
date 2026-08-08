using NovAcces.Domain.Entities;

namespace NovAcces.Application.Abstractions;

public interface IScanConfirmationRequestRepository
{
    Task AddAsync(ScanConfirmationRequest request, CancellationToken ct);

    Task<ScanConfirmationRequest?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Demande Pending déjà ouverte pour cette visite + direction (évite un
    /// doublon si l'agent tape deux fois sur la même ligne avant la première
    /// notification/réponse — idempotence, pas une limite de sécurité).
    /// </summary>
    Task<ScanConfirmationRequest?> GetPendingForVisitAsync(Guid visitId, Domain.Enums.CheckpointDirection direction, CancellationToken ct);

    /// <summary>Demandes en attente du site courant (tenant résolu), les plus récentes d'abord — portail Sûreté.</summary>
    Task<IReadOnlyCollection<ScanConfirmationRequest>> GetPendingAsync(CancellationToken ct);

    /// <summary>
    /// Fait passer à Expired toute demande Pending dont le délai est dépassé.
    /// Retourne celles qui viennent d'expirer (pour notifier l'agent demandeur),
    /// jamais celles déjà expirées lors d'un appel précédent.
    /// </summary>
    Task<IReadOnlyCollection<ScanConfirmationRequest>> ExpireStaleAsync(DateTimeOffset now, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
