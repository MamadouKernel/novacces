using NovAcces.Domain.Enums;

namespace NovAcces.Domain.Entities;

/// <summary>
/// Journal d'audit des actions d'administration et de sûreté (section 8.5 du
/// CDC : traçabilité complète et non répudiable). Répond à la question « qui a
/// révoqué quoi, qui a modifié quel paramètre », que le journal des scans
/// (<see cref="ScanLogEntry"/>) ne couvre pas.
///
/// Écrit uniquement en ajout — jamais de mise à jour ni de suppression depuis
/// l'application. L'inaltérabilité est imposée au niveau base par un trigger
/// append-only posé au provisionnement du site (cf. TenantProvisioningService),
/// exactement comme scan_logs.
/// </summary>
public sealed class AdminAuditEntry
{
    public Guid Id { get; private set; }

    /// <summary>Identité de l'acteur (sub du JWT, jamais une valeur fournie par le client).</summary>
    public string Actor { get; private set; } = default!;

    public AdminAuditAction Action { get; private set; }

    /// <summary>Identifiant de l'objet visé (Guid de visite, d'exclusion…), opaque et corrélable.</summary>
    public string? TargetId { get; private set; }

    /// <summary>Contexte lisible de l'action (motif, décompte purgé…), sans exposer de PII superflue.</summary>
    public string Detail { get; private set; } = default!;

    public DateTimeOffset Timestamp { get; private set; }

    private AdminAuditEntry() { } // EF Core

    public static AdminAuditEntry Create(
        string actor, AdminAuditAction action, string? targetId, string detail, DateTimeOffset now)
    {
        return new AdminAuditEntry
        {
            Id = Guid.NewGuid(),
            Actor = string.IsNullOrWhiteSpace(actor) ? "inconnu" : actor,
            Action = action,
            TargetId = targetId,
            Detail = detail ?? string.Empty,
            Timestamp = now
        };
    }
}
