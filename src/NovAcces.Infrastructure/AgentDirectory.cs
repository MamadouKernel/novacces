using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Entities;
using NovAcces.Infrastructure.Persistence;

namespace NovAcces.Infrastructure;

/// <summary>
/// Annuaire des agents du site courant. Le PIN est haché avec le hacheur
/// d'ASP.NET Core Identity (PBKDF2 salé) — jamais stocké ni comparé en clair.
/// Toutes les requêtes passent par le DbContext cantonné au tenant.
/// </summary>
public sealed class AgentDirectory : IAgentDirectory
{
    private readonly NovAccesDbContext _db;

    // Le hacheur n'utilise pas l'instance passée : on peut le partager.
    private static readonly PasswordHasher<Agent> Hasher = new();

    public AgentDirectory(NovAccesDbContext db) => _db = db;

    public async Task<AgentIdentity?> VerifyAsync(string matricule, string pin, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        var m = (matricule ?? string.Empty).Trim();

        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Matricule == m && a.IsActive, ct);
        if (agent is null)
            return null;

        var result = Hasher.VerifyHashedPassword(agent, agent.PinHash, pin ?? string.Empty);
        if (result == PasswordVerificationResult.Failed)
            return null;

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
