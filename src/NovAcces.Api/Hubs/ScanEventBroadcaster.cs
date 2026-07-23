using Microsoft.AspNetCore.SignalR;
using NovAcces.Application.Abstractions;

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

    public Task BroadcastAsync(ScanBroadcastEvent scanEvent, CancellationToken ct) =>
        _hub.Clients.Group(_tenant.SiteId).SendAsync("ScanRecorded", scanEvent, ct);
}
