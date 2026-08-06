using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovAcces.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddTerminalEnrollmentManualCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ManualCodeHash",
                schema: "identity",
                table: "terminal_enrollment_tickets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_terminal_enrollment_tickets_ManualCodeHash",
                schema: "identity",
                table: "terminal_enrollment_tickets",
                column: "ManualCodeHash",
                unique: true,
                filter: "\"ManualCodeHash\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_terminal_enrollment_tickets_ManualCodeHash",
                schema: "identity",
                table: "terminal_enrollment_tickets");

            migrationBuilder.DropColumn(
                name: "ManualCodeHash",
                schema: "identity",
                table: "terminal_enrollment_tickets");
        }
    }
}
