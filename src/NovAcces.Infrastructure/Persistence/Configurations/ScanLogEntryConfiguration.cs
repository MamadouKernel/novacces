using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovAcces.Domain.Entities;

namespace NovAcces.Infrastructure.Persistence.Configurations;

public sealed class ScanLogEntryConfiguration : IEntityTypeConfiguration<ScanLogEntry>
{
    public void Configure(EntityTypeBuilder<ScanLogEntry> builder)
    {
        builder.ToTable("scan_logs");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.VisitorName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.CheckpointId).HasMaxLength(100);
        builder.Property(e => e.AgentId).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Detail).HasMaxLength(1000).IsRequired();

        builder.HasIndex(e => e.Timestamp);
        builder.HasIndex(e => e.IsSecurityEvent);

        // INALTÉRABILITÉ (section 8.5 du CDC) — appliquée hors EF Core, par
        // TenantProvisioningService au moment du provisionnement du site :
        //  1. des triggers PostgreSQL interdisent DELETE, TRUNCATE et tout
        //     UPDATE autre que l'anonymisation du nom du visiteur. Ils
        //     s'exécutent quel que soit le rôle, superutilisateur compris :
        //     c'est la garantie réelle ;
        //  2. si Database:ApplicationRole désigne un rôle distinct du
        //     propriétaire des schémas, DELETE et TRUNCATE lui sont en outre
        //     retirés (seconde barrière si un trigger disparaissait).
        //
        // UPDATE ne peut PAS être retiré : la rétention (§7.3) doit pouvoir
        // remplacer le nom du visiteur par le sentinel d'anonymisation. C'est
        // le trigger, et non le système de privilèges, qui borne cet UPDATE à
        // cette seule transition.
    }
}
