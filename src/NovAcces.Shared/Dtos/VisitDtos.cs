namespace NovAcces.Shared.Dtos;

public sealed record CreateVisitRequestDto(
    string VisitorName,
    string VisitorCompany,
    string Motif,
    string Mode,          // "Unique" | "ThirtyDays"
    DateTimeOffset? ScheduledAt,
    int PlannedDurationMinutes,
    string? VisitorPhone,
    string? VisitorEmail);

public sealed record CreateVisitResponseDto(Guid VisitId, string SignedQrPayload, DateTimeOffset ExpiresAt, string? ManualCode = null);

// ---- Création groupée (invitation d'un groupe de visiteurs en une fois) ----

/// <summary>Lot de demandes à créer en une seule opération.</summary>
public sealed record BulkCreateVisitsRequestDto(IReadOnlyList<CreateVisitRequestDto> Visits);

/// <summary>Résultat d'une ligne du lot : QR généré, ou motif d'échec.</summary>
public sealed record BulkCreateVisitItemDto(
    string VisitorName,
    bool Success,
    Guid? VisitId,
    string? SignedQrPayload,
    DateTimeOffset? ExpiresAt,
    string? Error,
    string? ManualCode = null);

/// <summary>Synthèse d'une création groupée.</summary>
public sealed record BulkCreateVisitsResponseDto(
    int Created,
    int Failed,
    IReadOnlyList<BulkCreateVisitItemDto> Items);

/// <summary>Visite telle qu'affichée dans la liste du portail hôte.</summary>
public sealed record HostVisitDto(
    Guid VisitId,
    string VisitorName,
    string VisitorCompany,
    string Motif,
    string Mode,
    string Status,
    DateTimeOffset? ScheduledAt,
    int PlannedDurationMinutes,
    bool IsOnSite,
    DateTimeOffset CreatedAt,
    string? VisitorPhone = null,
    string? VisitorEmail = null,
    // Déjà visibles par l'hôte via /api/visits/{id}/history (mêmes données,
    // moindre privilège inchangé) — exposées ici aussi pour que la vue « du
    // jour » n'ait pas besoin d'un aller-retour par visite.
    DateTimeOffset? CheckedInAt = null,
    DateTimeOffset? CheckedOutAt = null);

/// <summary>
/// Correction des coordonnées d'une demande AVANT l'arrivée du visiteur
/// (erreur de saisie à la création) — voir Visit.UpdateVisitorDetails.
/// </summary>
public sealed record UpdateVisitRequestDto(
    string VisitorName,
    string VisitorCompany,
    string Motif,
    string? VisitorPhone,
    string? VisitorEmail);

/// <summary>Visiteur connu pour l'autocomplétion (pré-remplissage).</summary>
public sealed record KnownVisitorDto(
    string Name, string Company, string Motif, int PlannedDurationMinutes);

// ---- Historique / chronologie d'une demande de visite ----

/// <summary>Un événement de la vie d'une demande (créée, entrée, sortie, révoquée…).</summary>
public sealed record VisitEventDto(DateTimeOffset At, string Label, string? Detail, string Kind);

/// <summary>Chronologie complète d'une demande.</summary>
public sealed record VisitHistoryDto(
    Guid VisitId, string VisitorName, string Status, IReadOnlyList<VisitEventDto> Events);

// ---- Événement temps réel personnel de l'hôte (SignalR, groupe host:{id}) ----

/// <summary>
/// Diffusé au portail hôte quand SON visiteur entre, sort, ou déclenche une
/// suspicion de copie — Kind reprend les valeurs de HostEventKind ("Arrival",
/// "Departure", "SuspectedDuplicate") en chaîne, pour rester un DTO Shared
/// simple sans dépendre du type Application côté client Blazor.
/// </summary>
public sealed record HostVisitEventDto(
    Guid VisitId, string VisitorName, string Kind, DateTimeOffset OccurredAt,
    int? PresenceMinutes, int? OverstayMinutes);
