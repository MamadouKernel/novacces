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
    AgentCreated,

    /// <summary>Désactivation d'un agent sur un site (départ, réaffectation vers un autre site).</summary>
    AgentDeactivated,

    /// <summary>Enrôlement d'un terminal (clé API générée pour un ou plusieurs sites).</summary>
    TerminalCreated,

    /// <summary>Création d'un ticket QR d'enrôlement temporaire.</summary>
    EnrollmentTicketCreated,

    /// <summary>Activation d'un terminal sur un appareil précis.</summary>
    TerminalActivated,

    /// <summary>Révocation d'un terminal (clé désactivée).</summary>
    TerminalRevoked,

    /// <summary>Ancienne action conservée pour les journaux historiques.</summary>
    AccountSelfDeleted,

    /// <summary>Désactivation logique d'un compte par un Admin/SuperAdmin.</summary>
    AccountDeactivated,

    /// <summary>Réactivation d'un compte désactivé (SuperAdmin uniquement).</summary>
    AccountReactivated,

    /// <summary>Édition du nom, du rôle ou du site de rattachement d'un compte.</summary>
    AccountUpdated,

    /// <summary>Réinitialisation forcée du mot de passe d'un compte par un administrateur.</summary>
    AccountPasswordReset,

    /// <summary>
    /// Sortie d'un visiteur enregistrée manuellement par la sûreté, sans scan
    /// au poste (§7). Action privilégiée : elle clôt un cycle et éteint les
    /// alertes de dépassement, elle doit donc être traçable nominativement.
    /// </summary>
    ManualCheckOut
}
