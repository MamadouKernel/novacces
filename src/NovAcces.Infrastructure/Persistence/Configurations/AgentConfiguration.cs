using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovAcces.Domain.Entities;

namespace NovAcces.Infrastructure.Persistence.Configurations;

public sealed class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.ToTable("agents");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Matricule).HasMaxLength(40).IsRequired();
        builder.Property(a => a.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(a => a.PinHash).HasMaxLength(400).IsRequired();
        builder.Property(a => a.FailedPinAttempts).IsRequired();
        builder.Property(a => a.PinLockoutEnd);
        builder.Property(a => a.DeletedBy).HasMaxLength(200);

        // Matricule unique par site (schéma tenant), mais SEULEMENT parmi les
        // agents non supprimés : un matricule archivé (DeletedAt renseigné)
        // reste en base pour la traçabilité sans bloquer la création d'un
        // nouvel agent portant le même matricule (badge réattribué).
        builder.HasIndex(a => a.Matricule)
            .IsUnique()
            .HasFilter("\"DeletedAt\" IS NULL");
    }
}
