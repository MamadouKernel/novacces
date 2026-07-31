namespace NovAcces.Infrastructure;

/// <summary>
/// Paramètres de protection contre les essais PIN automatisés.
/// </summary>
public sealed class AgentSecurityOptions
{
    public int MaxPinFailures { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;

    public TimeSpan Lockout => TimeSpan.FromMinutes(Math.Clamp(LockoutMinutes, 1, 1_440));
}
