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
    string Detail);

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
