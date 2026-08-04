using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovAcces.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_registry",
                schema: "identity",
                columns: table => new
                {
                    Matricule = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SiteId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_registry", x => x.Matricule);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_registry_SiteId",
                schema: "identity",
                table: "agent_registry",
                column: "SiteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_registry",
                schema: "identity");
        }
    }
}
