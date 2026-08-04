using Microsoft.EntityFrameworkCore;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Identity;

/// <summary>
/// Implémentation de <see cref="IAgentRegistry"/> sur le schéma partagé
/// « identity ». La réclamation utilise SQL brut (INSERT ... ON CONFLICT DO
/// NOTHING) plutôt que le suivi de changements EF : c'est la seule façon
/// d'obtenir un « claim » atomique en un aller-retour base, sans fenêtre où
/// deux requêtes concurrentes pourraient toutes les deux croire avoir réussi.
/// </summary>
public sealed class AgentRegistry : IAgentRegistry
{
    private readonly NovAccesIdentityDbContext _db;
    private readonly IDateTimeProvider _clock;

    public AgentRegistry(NovAccesIdentityDbContext db, IDateTimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<string?> TryClaimAsync(string matricule, string siteId, CancellationToken ct)
    {
        var m = matricule.Trim();

        // Colonnes en PascalCase (comme toutes les entités EF de ce projet,
        // ex. VisitRepository/DataRetentionService) : le SQL brut doit les
        // guillemeter, sinon Postgres les replie en minuscules et échoue à
        // trouver "matricule" (colonne réellement nommée "Matricule").
        var rows = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO identity.agent_registry ("Matricule", "SiteId", "ClaimedAt")
             VALUES ({m}, {siteId}, {_clock.UtcNow})
             ON CONFLICT ("Matricule") DO NOTHING
             """,
            ct);

        if (rows == 1)
            return null;

        return await _db.AgentRegistry
            .AsNoTracking()
            .Where(r => r.Matricule == m)
            .Select(r => r.SiteId)
            .FirstOrDefaultAsync(ct);
    }

    public Task ReleaseAsync(string matricule, string siteId, CancellationToken ct)
    {
        var m = matricule.Trim();
        return _db.AgentRegistry
            .Where(r => r.Matricule == m && r.SiteId == siteId)
            .ExecuteDeleteAsync(ct);
    }
}
