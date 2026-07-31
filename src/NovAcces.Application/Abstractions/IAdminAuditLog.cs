using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;

namespace NovAcces.Application.Abstractions;

/// <summary>
/// Journal d'audit des actions d'administration/sûreté (section 8.5 du CDC).
/// Append-only : une entrée écrite ne peut être ni modifiée ni supprimée
/// (garanti au niveau base). Cantonné au tenant courant, comme le reste des
/// données du site.
/// </summary>
public interface IAdminAuditLog
{
    /// <summary>Enregistre une action privilégiée. L'horodatage est posé côté serveur.</summary>
    Task RecordAsync(AdminAuditAction action, string actor, string? targetId, string detail, CancellationToken ct);

    /// <summary>Retourne les entrées les plus récentes (consultation Sûreté/Admin).</summary>
    Task<IReadOnlyList<AdminAuditEntry>> GetRecentAsync(int limit, CancellationToken ct);
}
