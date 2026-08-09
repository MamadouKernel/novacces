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

    /// <summary>Réactivation d'un agent sur un site qu'il avait quitté (retour d'affectation).</summary>
    AgentReactivated,

    /// <summary>Suppression logique (archivage) d'un agent déjà désactivé — matricule libéré pour réutilisation.</summary>
    AgentDeleted,

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

    /// <summary>Suppression logique (archivage) d'un compte déjà désactivé — email réutilisable ensuite.</summary>
    AccountDeleted,

    /// <summary>
    /// Sortie d'un visiteur enregistrée manuellement par la sûreté, sans scan
    /// au poste (§7). Action privilégiée : elle clôt un cycle et éteint les
    /// alertes de dépassement, elle doit donc être traçable nominativement.
    /// </summary>
    ManualCheckOut,

    /// <summary>Correction des coordonnées d'un visiteur (nom, société, motif, contacts) avant son arrivée.</summary>
    VisitUpdated,

    /// <summary>
    /// Désactivation d'un site (contrat non reconduit) : coupe l'accès sans
    /// supprimer les données. Voir TenantResolutionMiddleware.
    /// </summary>
    SiteDeactivated,

    /// <summary>Réactivation d'un site désactivé (SuperAdmin uniquement).</summary>
    SiteReactivated,

    /// <summary>
    /// Suppression logique (archivage) d'un site déjà désactivé. Contrairement
    /// à un agent ou un compte, l'identifiant N'EST PAS réutilisable : le
    /// schéma et les données du site ne sont jamais détruits par cette action.
    /// </summary>
    SiteDeleted,

    /// <summary>Suppression logique (archivage) d'un terminal déjà révoqué — device réutilisable ensuite.</summary>
    TerminalDeleted,

    /// <summary>Affectation (ou retrait) d'un terminal à un poste — un poste peut regrouper plusieurs terminaux.</summary>
    TerminalCheckpointAssigned,

    /// <summary>
    /// Décision de la sûreté sur une demande de confirmation (validation sans
    /// QR/code, liste « Attendus »). Le detail précise l'agent demandeur ET,
    /// pour une approbation, le verdict RÉEL du scan (peut différer de la
    /// décision sûreté — voir ApproveConfirmationRequestHandler).
    /// </summary>
    ConfirmationRequestApproved,
    ConfirmationRequestDenied,

    /// <summary>Demande de confirmation expirée sans réponse de la sûreté — refus implicite (décision du 08/08/2026).</summary>
    ConfirmationRequestExpired,

    /// <summary>
    /// Entrée autorisée par la sûreté malgré une correspondance sur la liste
    /// d'exclusion (cas homonyme) — voir OverrideExclusionAndEnterCommand.
    /// Le detail porte la justification saisie ET le verdict réel du scan
    /// (peut différer si une autre règle a entre-temps refusé l'accès).
    /// </summary>
    ExclusionOverridden
}
