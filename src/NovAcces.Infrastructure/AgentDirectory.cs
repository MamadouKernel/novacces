using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Entities;
using NovAcces.Infrastructure.Persistence;

namespace NovAcces.Infrastructure;

public sealed class AgentDirectory : IAgentDirectory
{
    private readonly NovAccesDbContext _db;
    private readonly IOptions<AgentSecurityOptions> _security;
    private readonly IDateTimeProvider _clock;

    private static readonly PasswordHasher<Agent> Hasher = new();
    private static readonly Agent DummyAgent = Agent.Create("__dummy__", "__dummy__", string.Empty, DateTimeOffset.UnixEpoch);
    private static readonly string DummyPinHash = Hasher.HashPassword(DummyAgent, "__dummy-pin__");

    public AgentDirectory(NovAccesDbContext db, IOptions<AgentSecurityOptions> security, IDateTimeProvider clock)
    {
        _db = db;
        _security = security;
        _clock = clock;
    }

    public async Task<AgentIdentity?> VerifyAsync(string matricule, string pin, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        var m = (matricule ?? string.Empty).Trim();

        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Matricule == m, ct);
        if (agent is null || !agent.IsActive)
        {
            Hasher.VerifyHashedPassword(DummyAgent, DummyPinHash, pin ?? string.Empty);
            return null;
        }

        var now = _clock.UtcNow;
        if (agent.IsPinLocked(now))
        {
            Hasher.VerifyHashedPassword(agent, agent.PinHash, pin ?? string.Empty);
            return null;
        }

        var result = Hasher.VerifyHashedPassword(agent, agent.PinHash, pin ?? string.Empty);
        if (result == PasswordVerificationResult.Failed)
        {
            agent.RegisterFailedPin(now, _security.Value.MaxPinFailures, _security.Value.Lockout);
            await _db.SaveChangesAsync(ct);
            return null;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            agent.UpdatePin(Hasher.HashPassword(agent, pin ?? string.Empty));
        else
            agent.ResetPinFailures();

        await _db.SaveChangesAsync(ct);
        return new AgentIdentity(agent.Matricule, agent.DisplayName);
    }

    public async Task AddAsync(string matricule, string displayName, string pin, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        var m = matricule.Trim();

        if (await _db.Agents.AnyAsync(a => a.Matricule == m, ct))
            throw new InvalidOperationException($"Un agent avec le matricule « {m} » existe déjà sur ce site.");

        var agent = Agent.Create(m, displayName, pinHash: string.Empty, DateTimeOffset.UtcNow);
        agent.UpdatePin(Hasher.HashPassword(agent, pin));

        _db.Agents.Add(agent);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AgentIdentity>> ListAsync(CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        return await _db.Agents
            .OrderBy(a => a.Matricule)
            .Select(a => new AgentIdentity(a.Matricule, a.DisplayName))
            .ToListAsync(ct);
    }
}