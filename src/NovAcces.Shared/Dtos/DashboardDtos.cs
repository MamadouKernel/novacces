namespace NovAcces.Shared.Dtos;

/// <summary>
/// Événement de scan diffusé en temps réel au dashboard sûreté (SignalR,
/// message « ScanRecorded »). Type partagé pour que l'API et le portail web
/// s'accordent sur la forme exacte du message.
/// </summary>
public sealed record ScanEventDto(
    Guid VisitId,
    string VisitorName,
    string VerdictCode,
    bool IsGranted,
    bool IsCheckOut,
    bool IsSecurityEvent,
    string AgentId,
    DateTimeOffset OccurredAt);

/// <summary>Ligne du journal des scans (dashboard sûreté).</summary>
public sealed record ScanJournalEntryDto(
    DateTimeOffset Timestamp,
    string VisitorName,
    string AgentId,
    string Direction,
    bool WasGranted,
    bool WasCheckOut,
    bool IsSecurityEvent,
    string Detail,
    string AuthMethod = "Qr",
    // Nom d'affichage de l'hôte qui a créé la demande de visite scannée —
    // distinct d'AgentId (l'agent qui a scanné au poste). Null si l'hôte est
    // introuvable (compte supprimé, compte de service) : jamais une erreur.
    string? CreatedByDisplayName = null);

/// <summary>
/// Ligne de la vue « toutes les demandes du site » (dashboard sûreté) : à la
/// différence du journal (scans effectués) ou de « présents » (sur site MAINTENANT),
/// couvre TOUTE demande créée, quel que soit son statut ou si elle a même été
/// scannée — et indique qui l'a créée (moindre privilège : l'hôte lui-même ne
/// voit que SES propres demandes via /api/visits/mine, la Sûreté voit celles
/// de TOUT le site).
/// </summary>
public sealed record VisitListEntryDto(
    Guid VisitId,
    string VisitorName,
    string VisitorCompany,
    string Motif,
    string Mode,
    string Status,
    DateTimeOffset CreatedAt,
    string CreatedByDisplayName,
    string? CreatedByEmail,
    DateTimeOffset? ScheduledAt,
    int PlannedDurationMinutes,
    bool IsOnSite,
    DateTimeOffset? CheckedInAt,
    DateTimeOffset? CheckedOutAt,
    string? RevokedBy,
    DateTimeOffset? RevokedAt);

/// <summary>
/// Visiteur actuellement présent sur site (avec état de dépassement).
/// PlannedDurationMinutes permet au client de calculer une alerte prédictive
/// (« bientôt en dépassement ») avant que le seuil dur ne soit atteint —
/// calcul d'affichage seulement, l'escalade réelle reste dans Visit.cs.
/// </summary>
public sealed record OnSiteVisitorDto(
    Guid VisitId,
    string VisitorName,
    string VisitorCompany,
    DateTimeOffset? CheckedInAt,
    int OverstayMinutes,
    int OverstayLevel,
    int PlannedDurationMinutes);

/// <summary>
/// Alerte de dépassement de durée diffusée en temps réel au dashboard sûreté
/// (SignalR, message « OverstayAlert »). À partir du niveau 3, IsSecurityEvent.
/// </summary>
public sealed record OverstayAlertDto(
    Guid VisitId,
    string VisitorName,
    int OverstayMinutes,
    int Level,
    bool IsSecurityEvent,
    DateTimeOffset OccurredAt);

/// <summary>
/// Événement de scan diffusé au canal global « tous sites » (SignalR, message
/// « AdminActivity », réservé Admin/SuperAdmin). Volontairement sans nom de
/// visiteur : le dashboard Admin donne une santé agrégée par site, pas un
/// suivi individuel — le détail par visiteur reste au dashboard sûreté de
/// CE site (moindre privilège).
/// </summary>
public sealed record AdminScanActivityDto(
    string SiteId,
    bool IsGranted,
    bool IsCheckOut,
    bool IsSecurityEvent,
    DateTimeOffset OccurredAt);

/// <summary>
/// Alerte de dépassement diffusée au canal global « tous sites » (SignalR,
/// message « AdminOverstayAlert », réservé Admin/SuperAdmin). Même principe
/// que AdminScanActivityDto : volontairement SANS nom de visiteur — l'Admin
/// est alerté qu'un site a un dépassement à surveiller, le détail nominatif
/// reste au dashboard sûreté de ce site (moindre privilège).
/// </summary>
public sealed record AdminOverstayAlertDto(
    string SiteId,
    int Level,
    bool IsSecurityEvent,
    DateTimeOffset OccurredAt);

/// <summary>
/// Signale, au même canal global que <see cref="AdminScanActivityDto"/>,
/// qu'une entité gérée depuis la console (site/agent/terminal/compte) a
/// changé — pour rafraîchir un tableau ouvert sans rechargement de page.
/// </summary>
public sealed record AdminEntityChangedDto(string Kind, DateTimeOffset OccurredAt);

/// <summary>Synthèse du jour (dashboard sûreté), avec appréciation et recommandation.</summary>
public sealed record DashboardSummaryDto(
    int ScansToday,
    int EntriesGranted,
    int Exits,
    int Denied,
    int SecurityEvents,
    int OnSite,
    int? PeakHour,
    string RefusalAppreciation,
    string Recommendation,
    IReadOnlyList<int> HourlyScans);   // 24 tranches (index = heure locale) — courbe d'affluence

/// <summary>Entrée de la liste d'exclusion (vue sûreté, motif inclus).</summary>
public sealed record ExclusionDto(
    Guid Id, string DisplayName, string Reason, string AddedBy, DateTimeOffset CreatedAt);

/// <summary>Ajout à la liste d'exclusion.</summary>
public sealed record AddExclusionRequestDto(string DisplayName, string Reason);
