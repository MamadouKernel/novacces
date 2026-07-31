using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovAcces.Domain.Entities;

namespace NovAcces.Infrastructure.Persistence.Configurations;

public sealed class ExclusionEntryConfiguration : IEntityTypeConfiguration<ExclusionEntry>
{
    public void Configure(EntityTypeBuilder<ExclusionEntry> builder)
    {
        builder.ToTable("exclusion_entries");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.NormalizedName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Reason).HasMaxLength(500).IsRequired();
        builder.Property(e => e.AddedBy).HasMaxLength(100).IsRequired();

        // La comparaison au moment de la création d'une visite se fait sur le
        // nom normalisé : index pour la performance et l'unicité fonctionnelle.
        builder.HasIndex(e => e.NormalizedName);
    }
}
