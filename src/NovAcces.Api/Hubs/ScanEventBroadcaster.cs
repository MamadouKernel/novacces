using Microsoft.AspNetCore.SignalR;
using NovAcces.Application.Abstractions;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Hubs;

public sealed class ScanEventBroadcaster : IScanEventBroadcaster
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

        return _hub.Clients.Group(_tenant.SiteId).SendAsync("ScanRecorded", dto, ct);
    }

    public Task BroadcastOverstayAsync(OverstayBroadcastEvent overstay, CancellationToken ct)
    {
        var dto = new OverstayAlertDto(
            overstay.VisitId, overstay.VisitorName, overstay.OverstayMinutes,
            overstay.Level, overstay.IsSecurityEvent, overstay.OccurredAt);

        return _hub.Clients.Group(_tenant.SiteId).SendAsync("OverstayAlert", dto, ct);
    }
}
