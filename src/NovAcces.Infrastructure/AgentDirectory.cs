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
    private readonly IAgentRegistry _registry;
    private readonly ICurrentTenant _tenant;

    private static readonly PasswordHasher<Agent> Hasher = new();
    private static readonly Agent DummyAgent = Agent.Create("__dummy__", "__dummy__", string.Empty, DateTimeOffset.UnixEpoch);
    private static readonly string DummyPinHash = Hasher.HashPassword(DummyAgent, "__dummy-pin__");

    public AgentDirectory(
        NovAccesDbContext db, IOptions<AgentSecurityOptions> security, IDateTimeProvider clock,
        IAgentRegistry registry, ICurrentTenant tenant)
    {
        _db = db;
        _security = security;
        _clock = clock;
        _registry = registry;
        _tenant = tenant;
    }

    public async Task<AgentIdentity?> VerifyAsync(string matricule, string pin, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        var m = (matricule ?? string.Empty).Trim();

        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Matricule == m && a.DeletedAt == null, ct);
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

        if (await _db.Agents.AnyAsync(a => a.Matricule == m && a.DeletedAt == null, ct))
            throw new InvalidOperationException($"Un agent avec le matricule « {m} » existe déjà sur ce site.");

        // Réclamation globale AVANT création : un matricule ne peut jamais
        // être actif sur deux sites en même temps (§8.5, traçabilité
        // individuelle des scans). Garanti par une contrainte d'unicité en
        // base (voir AgentRegistry), pas par une vérification applicative.
        var otherSite = await _registry.TryClaimAsync(m, _tenant.SiteId, ct);
        if (otherSite is not null)
            throw new InvalidOperationException(
                $"Le matricule « {m} » est déjà actif sur le site « {otherSite} ». " +
                "Désactivez-le d'abord sur ce site, ou utilisez la réaffectation.");

        try
        {
            var agent = Agent.Create(m, displayName, pinHash: string.Empty, DateTimeOffset.UtcNow);
            agent.UpdatePin(Hasher.HashPassword(agent, pin));

            _db.Agents.Add(agent);
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            await _registry.ReleaseAsync(m, _tenant.SiteId, ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<AgentIdentity>> ListAsync(CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        return await _db.Agents
            .Where(a => a.DeletedAt == null)
            .OrderBy(a => a.Matricule)
            .Select(a => new AgentIdentity(a.Matricule, a.DisplayName, a.IsActive))
            .ToListAsync(ct);
    }

    public async Task<bool> DeactivateAsync(string matricule, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        var m = (matricule ?? string.Empty).Trim();

        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Matricule == m && a.DeletedAt == null, ct);
        if (agent is null)
            return false;

        agent.Deactivate();
        await _db.SaveChangesAsync(ct);

        // Libère la réclamation globale : le matricule redevient disponible
        // pour n'importe quel site (y compris celui-ci, plus tard).
        await _registry.ReleaseAsync(m, _tenant.SiteId, ct);
        return true;
    }

    public async Task<bool> ReactivateAsync(string matricule, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        var m = (matricule ?? string.Empty).Trim();

        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Matricule == m && a.DeletedAt == null, ct);
        if (agent is null)
            return false;

        // Même garde-fou qu'à la création : un retour sur ce site ne doit
        // pas laisser le matricule actif ailleurs en même temps.
        var otherSite = await _registry.TryClaimAsync(m, _tenant.SiteId, ct);
        if (otherSite is not null)
            throw new InvalidOperationException(
                $"Le matricule « {m} » est déjà actif sur le site « {otherSite} ». " +
                "Désactivez-le d'abord sur ce site, ou utilisez la réaffectation.");

        agent.Reactivate();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(string matricule, string actor, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        var m = (matricule ?? string.Empty).Trim();

        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Matricule == m && a.DeletedAt == null, ct);
        if (agent is null)
            return false;

        agent.Delete(_clock.UtcNow, actor);
        await _db.SaveChangesAsync(ct);

        // Défensif : la réclamation a normalement déjà été libérée à la
        // désactivation (Delete() exige !IsActive), donc no-op ici la
        // plupart du temps — mais coûte rien de s'assurer qu'elle ne traîne
        // jamais après une suppression.
        await _registry.ReleaseAsync(m, _tenant.SiteId, ct);
        return true;
    }

    public async Task<IReadOnlyList<ArchivedAgentIdentity>> ListArchivedAsync(CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        return await _db.Agents
            .Where(a => a.DeletedAt != null)
            .OrderByDescending(a => a.DeletedAt)
            .Select(a => new ArchivedAgentIdentity(a.Matricule, a.DisplayName, a.DeletedAt!.Value, a.DeletedBy))
            .ToListAsync(ct);
    }
}