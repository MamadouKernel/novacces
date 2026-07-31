using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovAcces.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Ceinture de sécurité au niveau base pour le garde-fou anti-doublon
    /// (une seule demande active par visiteur, nom + société). L'expression
    /// lower(btrim(...)) DOIT rester identique à la normalisation appliquée
    /// dans VisitRepository.HasActiveVisitForVisitorAsync (comparaison
    /// applicative) — voir le commentaire sur ActiveVisitorIndexName.
    /// Non représentable en Fluent API (fonctions SQL), d'où le SQL brut.
    /// </summary>
    public partial class AddActiveVisitorUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX "IX_visits_ActiveVisitorKey" ON visits (
                    lower(btrim("VisitorName")),
                    lower(btrim("VisitorCompany"))
                ) WHERE "Status" = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_visits_ActiveVisitorKey";""");
        }
    }
}
