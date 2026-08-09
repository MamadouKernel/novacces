using Microsoft.EntityFrameworkCore;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Identity;

/// <summary>
/// Résout les coordonnées d'un hôte depuis le magasin d'identité partagé.
///
/// <c>Visit.HostUserId</c> contient l'identifiant stable du principal
/// (claim <c>sub</c>), donc un GUID pour un compte réel. Une visite créée par
/// un compte de service ou importée pourrait porter autre chose : on répond
/// alors null plutôt que de lever, car ne pas pouvoir prévenir l'hôte n'est
/// jamais une raison de faire échouer un scan.
/// </summary>
public sealed class HostDirectory : IHostDirectory
{
    private readonly NovAccesIdentityDbContext _db;

    public HostDirectory(NovAccesIdentityDbContext db) => _db = db;

    public async Task<HostContact?> FindAsync(string hostUserId, CancellationToken ct)
    {
        if (!Guid.TryParse(hostUserId, out var userId))
            return null;

        var user = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId && !u.IsDeactivated)
            .Select(u => new { u.DisplayName, u.Email, u.PhoneNumber })
            .FirstOrDefaultAsync(ct);

        return user is null
            ? null
            : new HostContact(user.DisplayName, user.Email, user.PhoneNumber);
    }

    public async Task<IReadOnlyDictionary<string, HostContact>> FindManyAsync(
        IReadOnlyCollection<string> hostUserIds, CancellationToken ct)
    {
        // hostUserId non-GUID (compte de service, donnée importée) : écarté en
        // amont, ANY(...) sur une colonne Guid ne peut pas les recevoir de
        // toute façon — même comportement que FindAsync (null, jamais une erreur).
        var parsed = hostUserIds.Distinct()
            .Select(raw => (Raw: raw, Ok: Guid.TryParse(raw, out var id), Id: id))
            .Where(x => x.Ok)
            .ToList();

        if (parsed.Count == 0)
            return new Dictionary<string, HostContact>();

        var ids = parsed.Select(x => x.Id).ToList();
        var users = await _db.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id) && !u.IsDeactivated)
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.PhoneNumber })
            .ToListAsync(ct);

        var byId = users.ToDictionary(u => u.Id, u => new HostContact(u.DisplayName, u.Email, u.PhoneNumber));

        var result = new Dictionary<string, HostContact>();
        foreach (var p in parsed)
            if (byId.TryGetValue(p.Id, out var contact))
                result[p.Raw] = contact;

        return result;
    }
}
