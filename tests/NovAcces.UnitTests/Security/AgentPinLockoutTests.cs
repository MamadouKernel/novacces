using NovAcces.Domain.Entities;
using Xunit;

namespace NovAcces.UnitTests.Security;

public sealed class AgentPinLockoutTests
{
    [Fact]
    public void FailedPins_LockAgentAfterConfiguredThreshold()
    {
        var now = DateTimeOffset.UtcNow;
        var agent = Agent.Create("SG-0417", "Agent Test", "hash", now);

        agent.RegisterFailedPin(now, maxAttempts: 3, TimeSpan.FromMinutes(15));
        agent.RegisterFailedPin(now.AddSeconds(1), maxAttempts: 3, TimeSpan.FromMinutes(15));
        Assert.False(agent.IsPinLocked(now.AddSeconds(1)));

        agent.RegisterFailedPin(now.AddSeconds(2), maxAttempts: 3, TimeSpan.FromMinutes(15));

        Assert.True(agent.IsPinLocked(now.AddSeconds(2)));
        Assert.Equal(0, agent.FailedPinAttempts);
        Assert.True(agent.PinLockoutEnd > now.AddMinutes(14));
    }

    [Fact]
    public void SuccessfulAuthentication_ResetFailuresAndLockout()
    {
        var now = DateTimeOffset.UtcNow;
        var agent = Agent.Create("SG-0417", "Agent Test", "hash", now);
        agent.RegisterFailedPin(now, maxAttempts: 1, TimeSpan.FromMinutes(15));

        agent.ResetPinFailures();

        Assert.False(agent.IsPinLocked(now));
        Assert.Equal(0, agent.FailedPinAttempts);
        Assert.Null(agent.PinLockoutEnd);
    }
}
