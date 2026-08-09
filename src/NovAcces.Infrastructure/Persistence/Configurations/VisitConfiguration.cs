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
        // 80, pas 64 : depuis le 09/08/2026 l'empreinte durcie porte un préfixe
        // de format ("v2$" + 64 hex = 67 caractères) — voir ManualCodeService.
        builder.Property(v => v.ManualCodeHash).HasMaxLength(80);

        // Contrainte d'unicité qui matérialise l'anti-rejeu au niveau base :
        // même en cas de bug applicatif, PostgreSQL refuserait un doublon
        // de jeton de visite. Le verrou pessimiste (GetForUpdateAsync) reste
        // la protection primaire ; ceci est la ceinture de sécurité.
        builder.HasIndex(v => v.VisitToken).IsUnique();

        // Même raisonnement pour le code de secours, mais NULLABLE (une
        // visite n'a pas forcément de code assigné) — index unique PARTIEL,
        // qui ignore les NULL au lieu de n'autoriser qu'une seule visite
        // sans code sur tout le site.
        builder.HasIndex(v => v.ManualCodeHash).IsUnique().HasFilter("\"ManualCodeHash\" IS NOT NULL");

        // Champs utilisés pour la recherche/filtre côté sûreté et hôte.
        builder.HasIndex(v => v.HostUserId);
        builder.HasIndex(v => v.IsOnSite);

        // L'index unique partiel "une seule demande active par visiteur"
        // (nom + société, sur les valeurs normalisées) n'est PAS déclaré ici :
        // Fluent API ne sait pas exprimer lower(btrim(...)) sur une colonne.
        // Il est posé en SQL brut par la migration AddActiveVisitorUniqueIndex
        // — voir VisitRepository.HasActiveVisitForVisitorAsync (vérification
        // applicative, même normalisation) et VisitRepository.SaveChangesAsync
        // (traduction de la violation en DuplicateActiveVisitException).
    }
}
