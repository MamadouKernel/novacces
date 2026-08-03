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

    public DbSet<Terminal> Terminals => Set<Terminal>();
    public DbSet<TerminalEnrollmentTicketEntity> TerminalEnrollmentTickets => Set<TerminalEnrollmentTicketEntity>();
    public DbSet<RefreshSession> RefreshSessions => Set<RefreshSession>();
    public DbSet<ApplicationAuditEntry> ApplicationAudit => Set<ApplicationAuditEntry>();
    public DbSet<SiteRegistration> Sites => Set<SiteRegistration>();


    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<ApplicationUser>(u =>
        {
            u.Property(x => x.DisplayName).HasMaxLength(160).IsRequired();
            u.HasIndex(x => x.IsDeactivated);
        });

        builder.Entity<Terminal>(t =>
        {
            t.ToTable("terminals");
            t.HasKey(x => x.Id);
            t.Property(x => x.Label).HasMaxLength(200).IsRequired();
            t.Property(x => x.ApiKeyHash).HasMaxLength(64).IsRequired(); // hex SHA-256 = 64 caractères
            t.HasIndex(x => x.ApiKeyHash).IsUnique();

            // Liste de sites autorisés stockée en tableau Postgres natif
            // (text[]) — Npgsql la traduit directement, pas de table de
            // jointure nécessaire pour une simple liste de chaînes.
            t.Property(x => x.SiteIds).HasColumnType("text[]");
            t.Property(x => x.DeviceInstanceId).HasMaxLength(200);
            t.Property(x => x.DevicePublicKeyPem).HasMaxLength(12000);
            t.HasIndex(x => x.DeviceInstanceId).IsUnique();

        });
        builder.Entity<TerminalEnrollmentTicketEntity>(t =>
        {
            t.ToTable("terminal_enrollment_tickets");
            t.HasKey(x => x.Id);
            t.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            t.Property(x => x.CreatedBy).HasMaxLength(200).IsRequired();
            t.Property(x => x.DeviceInstanceId).HasMaxLength(200);
            t.HasIndex(x => x.TokenHash).IsUnique();
            t.HasIndex(x => new { x.TerminalId, x.ExpiresAt });
            t.HasOne<Terminal>().WithMany().HasForeignKey(x => x.TerminalId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ApplicationAuditEntry>(a =>
        {
            a.ToTable("application_audit");
            a.HasKey(x => x.Id);
            a.Property(x => x.Actor).HasMaxLength(100).IsRequired();
            a.Property(x => x.Method).HasMaxLength(16).IsRequired();
            a.Property(x => x.Path).HasMaxLength(300).IsRequired();
            a.Property(x => x.SiteId).HasMaxLength(40);
            a.Property(x => x.IpAddress).HasMaxLength(64);
            a.HasIndex(x => x.Timestamp);
            a.HasIndex(x => new { x.Actor, x.Timestamp });
        });
        builder.Entity<RefreshSession>(s =>
        {
            s.ToTable("refresh_sessions");
            s.HasKey(x => x.Id);
            s.Property(x => x.SubjectType).HasMaxLength(30).IsRequired();
            s.Property(x => x.SubjectId).HasMaxLength(200).IsRequired();
            s.Property(x => x.DisplayName).HasMaxLength(200);
            s.Property(x => x.SiteId).HasMaxLength(40);
            s.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            s.HasIndex(x => x.TokenHash).IsUnique();
            s.HasIndex(x => new { x.SubjectType, x.SubjectId });
            s.HasIndex(x => x.ExpiresAt);
        });
        builder.Entity<SiteRegistration>(s =>
        {
            s.ToTable("sites");
            s.HasKey(x => x.SiteId);
            s.Property(x => x.SiteId).HasMaxLength(40);
            s.Property(x => x.DeactivatedBy).HasMaxLength(200);
            s.Property(x => x.DeactivationReason).HasMaxLength(500);
            s.HasIndex(x => x.IsActive);
        });
    }
}
