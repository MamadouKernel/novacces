namespace NovAcces.Domain.Enums;

/// <summary>
/// Nature d'une action d'administration/sûreté tracée dans le journal d'audit
/// inaltérable (section 8.5 du CDC : « qui a révoqué quoi, qui a modifié quel
/// paramètre »). Distinct du journal des scans (ScanLogEntry), qui trace le
/// contrôle d'accès physique.
/// </summary>
public enum AdminAuditAction
{
    VisitRevoked,
    ExclusionAdded,
    ExclusionRemoved,
    DataPurged
}
