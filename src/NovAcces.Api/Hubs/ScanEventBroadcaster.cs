using Microsoft.AspNetCore.SignalR;
using NovAcces.Application.Abstractions;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Hubs;

public sealed class ScanEventBroadcaster : IScanEventBroadcaster, IAgentEventBroadcaster
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

        return _hub.Clients.Group(_tenant.SiteId).SendAsync("OverstayAlert", dto, ct);
    }

    public Task BroadcastVisitCreatedAsync(Guid visitId, string visitorName, DateTimeOffset occurredAt, CancellationToken ct) =>
        _hub.Clients.Group(_tenant.SiteId).SendAsync(
            "VisitCreated", new AgentVisitEventDto(visitId, visitorName, occurredAt), ct);

    public Task BroadcastVisitRevokedAsync(Guid visitId, DateTimeOffset occurredAt, CancellationToken ct) =>
        _hub.Clients.Group(_tenant.SiteId).SendAsync(
            "VisitRevoked", new AgentVisitEventDto(visitId, null, occurredAt), ct);
}
