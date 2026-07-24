using Microsoft.EntityFrameworkCore;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Entities;
using NovAcces.Infrastructure.Persistence.Configurations;

namespace NovAcces.Infrastructure.Persistence;

/// <summary>
/// DbContext scoped-per-request dont le schéma PostgreSQL actif dépend du
/// tenant résolu pour la requête en cours (REQ-F-10 : cloisonnement strict,
/// une base logique par client — ici, un schéma PostgreSQL dédié par client,
/// choix qui évite l'exploitation d'une flotte de bases physiques par un
/// développeur solo tout en garantissant l'étanchéité).
///
/// Mécanique : chaque entité est mappée SANS schéma fixe dans les
/// IEntityTypeConfiguration ; c'est ici, à l'ouverture de la connexion,
/// que le "search_path" PostgreSQL est positionné sur le schéma du tenant.
/// Toute requête EF Core exécutée sur ce contexte est donc automatiquement
/// et invisiblement cantonnée au bon schéma.
/// </summary>
public sealed class NovAccesDbContext : DbContext
{
    private readonly ICurrentTenant _tenant;

    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<ScanLogEntry> ScanLogs => Set<ScanLogEntry>();
    public DbSet<ExclusionEntry> ExclusionEntries => Set<ExclusionEntry>();

    public NovAccesDbContext(DbContextOptions<NovAccesDbContext> options, ICurrentTenant tenant)
        : base(options)
    {
        _tenant = tenant;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new VisitConfiguration());
        modelBuilder.ApplyConfiguration(new ScanLogEntryConfiguration());
        modelBuilder.ApplyConfiguration(new ExclusionEntryConfiguration());
    }

    /// <summary>
    /// Garde-fou : refuse toute opération sur les données tant que le tenant
    /// n'est pas résolu (défense en profondeur, en plus du middleware qui
    /// rejette déjà en 400 toute requête HTTP sans site — voir CLAUDE.md §7.3).
    ///
    /// C'est ici que le search_path ÉTAIT positionné, mais un simple "SET" ne
    /// survit pas au pooling de connexions Npgsql (état de session réinitialisé
    /// au retour au pool). L'application effective du schéma est désormais faite
    /// par TenantSchemaConnectionInterceptor, à chaque ouverture de connexion —
    /// robuste au pooling. Cette méthode ne fait donc plus qu'un contrôle
    /// d'invariant, sans aller-retour base.
    /// </summary>
    public Task EnsureTenantResolvedAsync(CancellationToken ct = default)
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException("Impossible d'exécuter une requête sans tenant résolu.");

        return Task.CompletedTask;
    }
}
