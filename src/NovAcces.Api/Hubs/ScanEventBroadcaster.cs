using Microsoft.AspNetCore.SignalR;
using NovAcces.Application.Abstractions;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Hubs;

public sealed class ScanEventBroadcaster : IScanEventBroadcaster, IAgentEventBroadcaster, IAdminActivityBroadcaster
{
    private readonly IHubContext<ScanEventsHub> _hub;
    private readonly ICurrentTenant _tenant;

    public ScanEventBroadcaster(IHubContext<ScanEventsHub> hub, ICurrentTenant tenant)
    {
        _hub = hub;
        _tenant = tenant;
    }

    public Task BroadcastAsync(ScanBroadcastEvent scanEvent, CancellationToken ct)
    {
        // Diffusion d'un DTO partagé (forme stable côté client web) au groupe du
        // site courant. Le cloisonnement tient : chaque client n'est abonné qu'au
        // groupe de son propre site (voir ScanEventsHub.OnConnectedAsync).
        var dto = new ScanEventDto(
            scanEvent.VisitId, scanEvent.VisitorName, scanEvent.VerdictCode,
            scanEvent.IsGranted, scanEvent.IsCheckOut, scanEvent.IsSecurityEvent,
            scanEvent.AgentId, scanEvent.OccurredAt);
        var siteTask = _hub.Clients.Group(_tenant.SiteId).SendAsync("ScanRecorded", dto, ct);

        // Écho allégé (sans nom de visiteur) au canal Admin global — voir
        // AdminScanActivityDto.
        var adminDto = new AdminScanActivityDto(
            _tenant.SiteId, scanEvent.IsGranted, scanEvent.IsCheckOut,
            scanEvent.IsSecurityEvent, scanEvent.OccurredAt);
        var globalTask = _hub.Clients.Group(ScanEventsHub.GlobalGroup).SendAsync("AdminActivity", adminDto, ct);

        return Task.WhenAll(siteTask, globalTask);
    }

    public Task BroadcastOverstayAsync(OverstayBroadcastEvent overstay, CancellationToken ct)
    {
        var dto = new OverstayAlertDto(
            overstay.VisitId, overstay.VisitorName, overstay.OverstayMinutes,
            overstay.Level, overstay.IsSecurityEvent, overstay.OccurredAt);

        // Sûreté : flux complet du site (nom compris — c'est son rôle).
        var siteTask = _hub.Clients.Group(_tenant.SiteId).SendAsync("OverstayAlert", dto, ct);

        // Admin/SuperAdmin : signal ANONYMISÉ (pas de nom, voir AdminOverstayAlertDto)
        // au canal global — même principe que l'écho AdminActivity de BroadcastAsync.
        var adminDto = new AdminOverstayAlertDto(_tenant.SiteId, overstay.Level, overstay.IsSecurityEvent, overstay.OccurredAt);
        var globalTask = _hub.Clients.Group(ScanEventsHub.GlobalGroup).SendAsync("AdminOverstayAlert", adminDto, ct);

        // Hôte : groupe personnel (host:{HostUserId}), jamais le groupe du site —
        // il ne doit voir QUE ses propres visiteurs (moindre privilège), avec le
        // nom cette fois puisque c'est bien SON visiteur.
        var hostTask = _hub.Clients.Group(HostGroup(overstay.HostUserId)).SendAsync("OverstayAlert", dto, ct);

        return Task.WhenAll(siteTask, globalTask, hostTask);
    }

    /// <summary>
    /// Nom du groupe SignalR personnel d'un hôte — DOIT rester identique à la
    /// jointure faite dans ScanEventsHub.OnConnectedAsync (mode ?hote=1).
    /// </summary>
    internal static string HostGroup(string hostUserId) => $"host:{hostUserId}";

    // Uniquement le groupe du site : la sûreté seule traite ces demandes
    // (SecurityJournal), pas de canal Admin/global — bruit inutile pour une
    // action opérationnelle propre à un site donné.
    public Task BroadcastConfirmationRequestedAsync(ConfirmationRequestedEvent requested, CancellationToken ct)
    {
        var dto = new ConfirmationRequestedDto(
            requested.RequestId, requested.VisitorName, requested.CheckpointId,
            requested.Direction.ToString(), requested.ExpiresAt);
        return _hub.Clients.Group(_tenant.SiteId).SendAsync("ConfirmationRequested", dto, ct);
    }

    public Task BroadcastConfirmationResolvedAsync(Guid requestId, CancellationToken ct) =>
        _hub.Clients.Group(_tenant.SiteId).SendAsync("ConfirmationResolved", requestId, ct);

    public Task BroadcastVisitCreatedAsync(Guid visitId, string visitorName, DateTimeOffset occurredAt, CancellationToken ct) =>
        _hub.Clients.Group(_tenant.SiteId).SendAsync(
            "VisitCreated", new AgentVisitEventDto(visitId, visitorName, occurredAt), ct);

    public Task BroadcastVisitRevokedAsync(Guid visitId, DateTimeOffset occurredAt, CancellationToken ct) =>
        _hub.Clients.Group(_tenant.SiteId).SendAsync(
            "VisitRevoked", new AgentVisitEventDto(visitId, null, occurredAt), ct);

    public Task NotifyEntityChangedAsync(string kind, CancellationToken ct) =>
        _hub.Clients.Group(ScanEventsHub.GlobalGroup).SendAsync(
            "AdminEntityChanged", new AdminEntityChangedDto(kind, DateTimeOffset.UtcNow), ct);
}
