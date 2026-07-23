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

    public NovAccesDbContext(DbContextOptions<NovAccesDbContext> options, ICurrentTenant tenant)
        : base(options)
    {
        _tenant = tenant;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new VisitConfiguration());
        modelBuilder.ApplyConfiguration(new ScanLogEntryConfiguration());
    }

    /// <summary>
    /// Positionne le search_path PostgreSQL sur le schéma du tenant résolu.
    /// Appelé explicitement par TenantSchemaInterceptor avant toute requête —
    /// voir DependencyInjection.cs. Le nom de schéma est validé et assaini
    /// dans CurrentTenant.Resolve() (whitelist alphanumérique) : la construction
    /// de commande ci-dessous n'est donc jamais exposée à une injection SQL
    /// provenant d'une entrée utilisateur non contrôlée.
    /// </summary>
    public async Task EnsureTenantSchemaAppliedAsync(CancellationToken ct = default)
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException("Impossible d'exécuter une requête sans tenant résolu.");

        await Database.ExecuteSqlRawAsync($"SET search_path TO {_tenant.SchemaName}, public", ct);
    }
}
