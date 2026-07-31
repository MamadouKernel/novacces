using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovAcces.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Matricule = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PinHash = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agents_Matricule",
                table: "agents",
                column: "Matricule",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agents");
        }
    }
}
