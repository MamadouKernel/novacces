using NovAcces.Application.Abstractions;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;

namespace NovAcces.Application.Visits;

public sealed record CreateVisitCommand(
    string VisitorName,
    string VisitorCompany,
    string Motif,
    string HostUserId,
    AccessMode Mode,
    DateTimeOffset? ScheduledAt,
    int PlannedDurationMinutes,
    string? VisitorPhone,
    string? VisitorEmail
);

public sealed record CreateVisitResult(Guid VisitId, string SignedQrPayload, DateTimeOffset ExpiresAt);

/// <summary>
/// Création d'une demande de visite et génération du QR signé.
/// Garde-fou anti-doublon appliqué en amont par l'appelant (Api) via une
/// recherche des visites actives du même visiteur — cf. maquette de démo.
/// </summary>
public sealed class CreateVisitHandler
{
    private readonly IVisitRepository _visits;
    private readonly IQrSigningService _signing;
    private readonly IDateTimeProvider _clock;
    private readonly IExclusionListService _exclusionList;

    public CreateVisitHandler(
        IVisitRepository visits,
        IQrSigningService signing,
        IDateTimeProvider clock,
        IExclusionListService exclusionList)
    {
        _visits = visits;
        _signing = signing;
        _clock = clock;
        _exclusionList = exclusionList;
    }

    public async Task<CreateVisitResult> HandleAsync(CreateVisitCommand command, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var isExcluded = await _exclusionList.IsExcludedAsync(command.VisitorName, ct);

        var visit = Visit.Create(
            command.VisitorName, command.VisitorCompany, command.Motif, command.HostUserId,
            command.Mode, command.ScheduledAt, command.PlannedDurationMinutes,
            command.VisitorPhone, command.VisitorEmail, isExcluded, now);

        var expiresAt = command.Mode == AccessMode.Unique
            ? command.ScheduledAt!.Value.AddMinutes(15)
            : now.AddDays(30);

        var signedPayload = _signing.SignVisitToken(visit.Id, visit.VisitToken, expiresAt);

        await _visits.AddAsync(visit, ct);
        await _visits.SaveChangesAsync(ct);

        return new CreateVisitResult(visit.Id, signedPayload, expiresAt);
    }
}

/// <summary>Vérification de la liste d'exclusion du site (REQ-F-11) — implémentation Infrastructure.</summary>
public interface IExclusionListService
{
    Task<bool> IsExcludedAsync(string visitorName, CancellationToken ct);
}
