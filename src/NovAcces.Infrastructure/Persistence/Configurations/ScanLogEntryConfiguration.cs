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
        builder.Property(e => e.AgentId).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Detail).HasMaxLength(1000).IsRequired();

        builder.HasIndex(e => e.Timestamp);
        builder.HasIndex(e => e.IsSecurityEvent);

        // NOTE DE PRODUCTION (à appliquer lors du script de provisionnement
        // d'un nouveau site, hors EF Core) : une fois la table créée, retirer
        // les droits UPDATE et DELETE de l'utilisateur applicatif sur
        // "scan_logs" au niveau PostgreSQL (GRANT INSERT, SELECT uniquement).
        // C'est cette contrainte au niveau base, et non le code C#, qui rend
        // le journal réellement inaltérable (section 8.5 du CDC).
    }
}
