using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovAcces.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_agents_Matricule",
                table: "agents");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                table: "agents",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_agents_Matricule",
                table: "agents",
                column: "Matricule",
                unique: true,
                filter: "\"DeletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_agents_Matricule",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                table: "agents");

            migrationBuilder.CreateIndex(
                name: "IX_agents_Matricule",
                table: "agents",
                column: "Matricule",
                unique: true);
        }
    }
}
