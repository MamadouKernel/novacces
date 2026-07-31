namespace NovAcces.Application.Abstractions;

/// <summary>Diffusion des changements de visites vers les applications agent.</summary>
public interface IAgentEventBroadcaster
{
    Task BroadcastVisitCreatedAsync(Guid visitId, string visitorName, DateTimeOffset occurredAt, CancellationToken ct);
    Task BroadcastVisitRevokedAsync(Guid visitId, DateTimeOffset occurredAt, CancellationToken ct);
}
