using Microsoft.EntityFrameworkCore;
using NovAcces.Infrastructure.Identity;

namespace NovAcces.Api.Endpoints;

/// <summary>
/// Résout un identifiant d'acteur brut (le <c>sub</c> du JWT, voir
/// <c>ClaimsPrincipal.HostIdentifier()</c>) en nom affiché, pour les écrans
/// où cet identifiant est montré tel quel (exclusions, journaux d'audit).
/// Best-effort : un acteur non résolu (terminal/agent, "anonymous", compte
/// supprimé) garde sa valeur brute plutôt que de disparaître.
/// </summary>
public static class ActorDisplayNames
{
    public static async Task<Dictionary<string, string>> ResolveAsync(
        NovAccesIdentityDbContext identityDb, IEnumerable<string> actorIds, CancellationToken ct)
    {
        var byGuid = actorIds.Distinct()
            .Select(raw => (Raw: raw, Parsed: Guid.TryParse(raw, out var g) ? g : (Guid?)null))
            .Where(x => x.Parsed is not null)
            .ToList();

        if (byGuid.Count == 0) return new();

        var ids = byGuid.Select(x => x.Parsed!.Value).ToList();
        var names = await identityDb.Users
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName })
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        return byGuid
            .Where(x => names.ContainsKey(x.Parsed!.Value))
            .ToDictionary(x => x.Raw, x => names[x.Parsed!.Value]);
    }
}
