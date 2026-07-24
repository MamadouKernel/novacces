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

/// <summary>Visiteur actuellement présent sur site (avec état de dépassement).</summary>
public sealed record OnSiteVisitorDto(
    Guid VisitId,
    string VisitorName,
    string VisitorCompany,
    DateTimeOffset? CheckedInAt,
    int OverstayMinutes,
    int OverstayLevel);

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
    string Recommendation);

/// <summary>Entrée de la liste d'exclusion (vue sûreté, motif inclus).</summary>
public sealed record ExclusionDto(
    Guid Id, string DisplayName, string Reason, string AddedBy, DateTimeOffset CreatedAt);

/// <summary>Ajout à la liste d'exclusion.</summary>
public sealed record AddExclusionRequestDto(string DisplayName, string Reason);
