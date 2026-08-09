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
    public DbSet<AgentRegistryEntry> AgentRegistry => Set<AgentRegistryEntry>();
    public DbSet<PushSubscriptionEntity> PushSubscriptions => Set<PushSubscriptionEntity>();


    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<ApplicationUser>(u =>
        {
            u.Property(x => x.DisplayName).HasMaxLength(160).IsRequired();
            u.Property(x => x.DeletedBy).HasMaxLength(200);
            u.HasIndex(x => x.IsDeactivated);

            // L'unicité de UserName/Email (« UserNameIndex », posée par la
            // classe de base Identity juste au-dessus) ne doit porter que sur
            // les comptes NON supprimés : un compte archivé garde sa ligne
            // pour la traçabilité, mais son email redevient réutilisable pour
            // un NOUVEAU compte (même principe que Agent.Matricule).
            u.HasIndex(x => x.NormalizedUserName).IsUnique().HasFilter("\"DeletedAt\" IS NULL");

            // Masque les comptes supprimés de TOUTES les requêtes LINQ par
            // défaut — y compris celles internes à UserManager/UserStore
            // (FindByEmailAsync, GetUsersInRoleAsync…), qu'on ne peut pas
            // filtrer au cas par cas. La liste des archivés (SuperAdmin)
            // utilise explicitement IgnoreQueryFilters().
            u.HasQueryFilter(x => x.DeletedAt == null);
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
            t.Property(x => x.DeletedBy).HasMaxLength(200);
            t.Property(x => x.ActiveShiftJti).HasMaxLength(36);
            t.Property(x => x.ActiveShiftMatricule).HasMaxLength(80);
            t.Property(x => x.CheckpointId).HasMaxLength(80);
            t.Property(x => x.DeviceModel).HasMaxLength(150);

            // Unique parmi les terminaux NON supprimés seulement : un appareil
            // physique dont le terminal a été archivé peut être réenrôlé comme
            // un NOUVEAU terminal (même principe que Agent.Matricule).
            t.HasIndex(x => x.DeviceInstanceId).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
        });
        builder.Entity<TerminalEnrollmentTicketEntity>(t =>
        {
            t.ToTable("terminal_enrollment_tickets");
            t.HasKey(x => x.Id);
            t.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            // 80, pas 64 : voir ManualCodeService (empreinte durcie "v2$" + 64 hex).
            t.Property(x => x.ManualCodeHash).HasMaxLength(80);
            t.Property(x => x.CreatedBy).HasMaxLength(200).IsRequired();
            t.Property(x => x.DeviceInstanceId).HasMaxLength(200);
            // Gabarit "poste" (TerminalId nul) — voir TerminalEnrollmentTicketEntity.
            t.Property(x => x.PosteLabel).HasMaxLength(120);
            t.Property(x => x.PosteSiteIds).HasColumnType("text[]");
            t.Property(x => x.PosteCheckpointId).HasMaxLength(80);
            t.HasIndex(x => x.TokenHash).IsUnique();
            t.HasIndex(x => x.ManualCodeHash).IsUnique().HasFilter("\"ManualCodeHash\" IS NOT NULL");
            t.HasIndex(x => new { x.TerminalId, x.ExpiresAt });
            // Nullable depuis le mode poste : un ticket réutilisable n'a pas
            // de terminal précis avant le premier scan (voir CreateForPoste).
            t.HasOne<Terminal>().WithMany().HasForeignKey(x => x.TerminalId).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
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
            s.Property(x => x.DeletedBy).HasMaxLength(200);
            s.HasIndex(x => x.IsActive);
        });
        builder.Entity<AgentRegistryEntry>(a =>
        {
            a.ToTable("agent_registry");
            a.HasKey(x => x.Matricule);
            a.Property(x => x.Matricule).HasMaxLength(80);
            a.Property(x => x.SiteId).HasMaxLength(40).IsRequired();
            a.HasIndex(x => x.SiteId);
        });
        builder.Entity<PushSubscriptionEntity>(p =>
        {
            p.ToTable("push_subscriptions");
            p.HasKey(x => x.Id);
            p.Property(x => x.Endpoint).HasMaxLength(600).IsRequired();
            p.Property(x => x.P256dh).HasMaxLength(200).IsRequired();
            p.Property(x => x.Auth).HasMaxLength(100).IsRequired();
            p.HasIndex(x => x.Endpoint).IsUnique();
            p.HasIndex(x => x.UserId);
            p.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Terminal>().Property(x => x.ExpoPushToken).HasMaxLength(300);
    }
}
