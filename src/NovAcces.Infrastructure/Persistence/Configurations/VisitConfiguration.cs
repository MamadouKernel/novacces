using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovAcces.Domain.Entities;

namespace NovAcces.Infrastructure.Persistence.Configurations;

public sealed class VisitConfiguration : IEntityTypeConfiguration<Visit>
{
    public void Configure(EntityTypeBuilder<Visit> builder)
    {
        builder.ToTable("visits");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.VisitorName).HasMaxLength(200).IsRequired();
        builder.Property(v => v.VisitorCompany).HasMaxLength(200).IsRequired();
        builder.Property(v => v.Motif).HasMaxLength(500);
        builder.Property(v => v.HostUserId).HasMaxLength(100).IsRequired();
        builder.Property(v => v.VisitorPhone).HasMaxLength(30);
        builder.Property(v => v.VisitorEmail).HasMaxLength(200);

        // Contrainte d'unicité qui matérialise l'anti-rejeu au niveau base :
        // même en cas de bug applicatif, PostgreSQL refuserait un doublon
        // de jeton de visite. Le verrou pessimiste (GetForUpdateAsync) reste
        // la protection primaire ; ceci est la ceinture de sécurité.
        builder.HasIndex(v => v.VisitToken).IsUnique();

        // Champs utilisés pour la recherche/filtre côté sûreté et hôte.
        builder.HasIndex(v => v.HostUserId);
        builder.HasIndex(v => v.IsOnSite);
    }
}
