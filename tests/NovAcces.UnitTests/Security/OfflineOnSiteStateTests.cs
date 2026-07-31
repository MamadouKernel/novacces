using NovAcces.Shared.Dtos;
using NovAcces.Shared.Offline;
using Xunit;

namespace NovAcces.UnitTests.Security;

/// <summary>
/// Reconstruction hors-ligne de l'état « sur site » : instantané serveur de la
/// liste signée + rejeu chronologique des scans locaux. C'est la base de
/// l'anti-rejeu et du cycle directionnel du mode dégradé.
/// </summary>
public class OfflineOnSiteStateTests
{
    private static OfflineListItem Item(Guid token, bool onSite) =>
        new(Guid.NewGuid(), token, null, false, onSite);

    private static OfflineScanDto Scan(Guid token, string direction, bool granted) =>
        new(token, direction, granted, DateTimeOffset.UtcNow.AddSeconds(Random.Shared.Next(1, 1000)));

    [Fact]
    public void ServerSnapshot_SeedsOnSite()
    {
        var onSiteToken = Guid.NewGuid();
        var offToken = Guid.NewGuid();

        var state = OfflineOnSiteState.Compute(
            new[] { Item(onSiteToken, true), Item(offToken, false) },
            Array.Empty<OfflineScanDto>());

        Assert.Contains(onSiteToken, state);
        Assert.DoesNotContain(offToken, state);
    }

    [Fact]
    public void GrantedEntry_AddsToOnSite_GrantedExit_Removes()
    {
        var token = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;

        var afterEntry = OfflineOnSiteState.Compute(
            Array.Empty<OfflineListItem>(),
            new[] { new OfflineScanDto(token, "Entry", true, at) });
        Assert.Contains(token, afterEntry);

        var afterExit = OfflineOnSiteState.Compute(
            Array.Empty<OfflineListItem>(),
            new[]
            {
                new OfflineScanDto(token, "Entry", true, at),
                new OfflineScanDto(token, "Exit", true, at.AddMinutes(30)),
            });
        Assert.DoesNotContain(token, afterExit);
    }

    [Fact]
    public void DeniedScan_DoesNotChangeState()
    {
        var token = Guid.NewGuid();

        var state = OfflineOnSiteState.Compute(
            Array.Empty<OfflineListItem>(),
            new[] { Scan(token, "Entry", granted: false) });

        Assert.DoesNotContain(token, state);
    }
}
