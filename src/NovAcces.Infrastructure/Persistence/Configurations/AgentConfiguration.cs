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

        // Matricule unique par site (schéma tenant).
        builder.HasIndex(a => a.Matricule).IsUnique();
    }
}
