namespace NovAcces.Shared.Dtos;

/// <summary>
/// Ligne « attendus aujourd'hui » pour l'agent (§11). Moindre privilège : nom +
/// statut + fenêtre horaire UNIQUEMENT — jamais motif, entreprise ni coordonnées.
/// </summary>
public sealed record ExpectedVisitorDto(
    string VisitorName,
    string Status,                 // attendu | sur site | sorti | révoqué
    DateTimeOffset? WindowStart,
    DateTimeOffset? WindowEnd);

/// <summary>Réponse exacte de la liste hors-ligne du contrat mobile.</summary>
public sealed record ContractOfflineListDto(
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<ContractOfflineVisitDto> Visits,
    string SignedList);

/// <summary>Projection minimale d'une visite utilisable hors connexion.</summary>
public sealed record ContractOfflineVisitDto(
    Guid VisitId,
    string Nom,
    string Mode,
    DateTimeOffset? FenetreDebut,
    DateTimeOffset? FenetreFin,
    string Statut,
    bool Present);

/// <summary>Scan remonté par l'application après une période hors-ligne.</summary>
public sealed record ContractOfflineScanDto(
    string SignedQrPayload,
    string Direction,
    string AgentId,
    DateTimeOffset ScannedAtUtc,
    string OfflineVerdict);

public sealed record ContractSyncConflictDto(Guid VisitId, string Raison);

public sealed record ContractScanSyncResponse(
    int Accepted,
    IReadOnlyList<ContractSyncConflictDto> Conflicts);

/// <summary>
/// Liste hors-ligne signée (§6) : le jeton signé à charger dans l'app agent, plus
/// les instants d'émission et d'expiration (TTL) affichés hors ligne.
/// </summary>
public sealed record OfflineListDto(
    string SignedList,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    int EntryCount);

// ---- Prise de poste de l'agent (matricule + PIN) ----

/// <summary>Prise de poste : l'agent s'identifie sur le terminal.</summary>
public sealed record ShiftStartRequestDto(string Matricule, string Pin);

/// <summary>Poste ouvert : identité de l'agent + jeton de poste à joindre aux scans.</summary>
public sealed record ShiftStartResponseDto(
    string Matricule, string DisplayName, string ShiftToken, DateTimeOffset ExpiresAt);

/// <summary>Création d'un agent (administration) : matricule + nom + PIN, pour un site.</summary>
public sealed record CreateAgentRequestDto(string SiteId, string Matricule, string DisplayName, string Pin);

/// <summary>Agent listé dans la console d'administration (sans le PIN).</summary>
public sealed record AgentSummaryDto(string Matricule, string DisplayName, bool IsActive = true);

/// <summary>Agent supprimé (archivé), pour la consultation en lecture seule.</summary>
public sealed record ArchivedAgentSummaryDto(
    string Matricule, string DisplayName, DateTimeOffset DeletedAt, string? DeletedBy);

/// <summary>
/// Réaffectation atomique d'un agent vers un autre site (nouveau PIN sur la
/// cible) : le serveur désactive la source avant de créer la cible, avec
/// compensation automatique en cas d'échec — jamais actif aux deux endroits.
/// </summary>
public sealed record ReassignAgentRequestDto(string TargetSiteId, string Pin);

/// <summary>
/// Un scan effectué hors ligne, remonté à la resynchronisation. Porte le verdict
/// local (VerdictCode) et le marqueur d'événement de sécurité, pour que le
/// serveur puisse journaliser fidèlement CHAQUE scan hors-ligne (REQ-F-07, §6.2),
/// accordé comme refusé, et pas seulement les conflits.
/// </summary>
/// <summary>
/// Scan réalisé hors ligne, remonté à la resynchronisation.
///
/// <see cref="SignedQrPayload"/> est le SEUL champ que le serveur croit : il
/// rejoue la vérification ES256 puis la règle métier complète. Les autres champs
/// (verdict local, jeton de visite) sont des informations du terminal — utiles
/// pour détecter un écart en ligne/hors ligne et pour l'état local, mais jamais
/// une autorisation. Sans cela, un terminal compromis pourrait inscrire des
/// « accès accordé » arbitraires dans un journal que rien ne peut effacer.
/// </summary>
public sealed record OfflineScanDto(
    Guid VisitToken,               // usage LOCAL au terminal (état « sur site ») — non fiable côté serveur
    string Direction,              // Entry | Exit
    bool WasGranted,               // verdict LOCAL, confronté au verdict serveur
    DateTimeOffset OccurredAt,
    string? VerdictCode = null,    // ex. « Recognized », « TooLate », « Expired »
    bool WasSecurityEvent = false,
    string SignedQrPayload = "");  // enveloppe ES256 d'origine — fait foi

/// <summary>Lot de scans hors-ligne à confronter au registre central.</summary>
public sealed record ResyncRequestDto(IReadOnlyList<OfflineScanDto> Scans);

/// <summary>
/// Résultat de la resynchronisation (§6.5) : nombre de scans confrontés et les
/// conflits détectés (ex. QR révoqué pendant la coupure) — événements de sécurité.
/// </summary>
public sealed record ResyncResultDto(
    int Processed,
    IReadOnlyList<ResyncConflictDto> Conflicts);

public sealed record ResyncConflictDto(
    Guid VisitToken,
    string VisitorName,
    string Reason,
    DateTimeOffset OccurredAt);
