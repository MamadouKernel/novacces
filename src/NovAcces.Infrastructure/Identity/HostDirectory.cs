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
}
