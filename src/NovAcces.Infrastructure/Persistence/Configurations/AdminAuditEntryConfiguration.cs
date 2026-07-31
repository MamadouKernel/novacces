using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovAcces.Domain.Entities;

namespace NovAcces.Infrastructure.Persistence.Configurations;

public sealed class AdminAuditEntryConfiguration : IEntityTypeConfiguration<AdminAuditEntry>
{
    public void Configure(EntityTypeBuilder<AdminAuditEntry> builder)
    {
        builder.ToTable("admin_audit");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Actor).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Action).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(e => e.TargetId).HasMaxLength(100);
        builder.Property(e => e.Detail).HasMaxLength(1000).IsRequired();

        // Consultation triée par date décroissante (audit récent en tête).
        builder.HasIndex(e => e.Timestamp);
    }
}
