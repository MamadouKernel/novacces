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
    DataPurged,

    /// <summary>Anonymisation des noms de visiteurs dans le journal des scans après la fenêtre de conservation (§7.3).</summary>
    JournalAnonymized,

    /// <summary>Création d'un agent (matricule + PIN) pour la prise de poste.</summary>
    AgentCreated
}
