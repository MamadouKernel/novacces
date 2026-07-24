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

/// <summary>Visiteur actuellement présent sur site.</summary>
public sealed record OnSiteVisitorDto(
    Guid VisitId,
    string VisitorName,
    string VisitorCompany,
    DateTimeOffset? CheckedInAt);

/// <summary>Synthèse du jour (dashboard sûreté).</summary>
public sealed record DashboardSummaryDto(
    int ScansToday,
    int EntriesGranted,
    int Exits,
    int Denied,
    int SecurityEvents,
    int OnSite);

/// <summary>Entrée de la liste d'exclusion (vue sûreté, motif inclus).</summary>
public sealed record ExclusionDto(
    Guid Id, string DisplayName, string Reason, string AddedBy, DateTimeOffset CreatedAt);

/// <summary>Ajout à la liste d'exclusion.</summary>
public sealed record AddExclusionRequestDto(string DisplayName, string Reason);
