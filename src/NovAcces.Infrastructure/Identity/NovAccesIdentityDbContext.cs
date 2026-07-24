using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace NovAcces.Infrastructure.Identity;

/// <summary>
/// Contexte EF Core des comptes/rôles. DISTINCT de NovAccesDbContext : il vit
/// dans un schéma FIXE et partagé (« identity »), sans l'intercepteur de tenant
/// — les identités sont transverses aux sites (cf. ApplicationUser.SiteId).
///
/// Un schéma dédié (et non « public ») garde la base lisible et évite toute
/// collision avec les schémas de sites (« site_* »).
/// </summary>
public sealed class NovAccesIdentityDbContext
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    public const string Schema = "identity";

    public NovAccesIdentityDbContext(DbContextOptions<NovAccesIdentityDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);
    }
}
