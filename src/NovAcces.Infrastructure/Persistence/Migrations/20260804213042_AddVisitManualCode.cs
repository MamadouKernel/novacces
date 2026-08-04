using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovAcces.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitManualCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ManualCodeHash",
                table: "visits",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AuthMethod",
                table: "scan_logs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_visits_ManualCodeHash",
                table: "visits",
                column: "ManualCodeHash",
                unique: true,
                filter: "\"ManualCodeHash\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_visits_ManualCodeHash",
                table: "visits");

            migrationBuilder.DropColumn(
                name: "ManualCodeHash",
                table: "visits");

            migrationBuilder.DropColumn(
                name: "AuthMethod",
                table: "scan_logs");
        }
    }
}
