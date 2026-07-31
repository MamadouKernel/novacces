using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovAcces.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceEnrollmentTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeviceInstanceId",
                schema: "identity",
                table: "terminals",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DevicePublicKeyPem",
                schema: "identity",
                table: "terminals",
                type: "character varying(12000)",
                maxLength: 12000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EnrolledAt",
                schema: "identity",
                table: "terminals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "terminal_enrollment_tickets",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeviceInstanceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_terminal_enrollment_tickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_terminal_enrollment_tickets_terminals_TerminalId",
                        column: x => x.TerminalId,
                        principalSchema: "identity",
                        principalTable: "terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_terminals_DeviceInstanceId",
                schema: "identity",
                table: "terminals",
                column: "DeviceInstanceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_terminal_enrollment_tickets_TerminalId_ExpiresAt",
                schema: "identity",
                table: "terminal_enrollment_tickets",
                columns: new[] { "TerminalId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_terminal_enrollment_tickets_TokenHash",
                schema: "identity",
                table: "terminal_enrollment_tickets",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "terminal_enrollment_tickets",
                schema: "identity");

            migrationBuilder.DropIndex(
                name: "IX_terminals_DeviceInstanceId",
                schema: "identity",
                table: "terminals");

            migrationBuilder.DropColumn(
                name: "DeviceInstanceId",
                schema: "identity",
                table: "terminals");

            migrationBuilder.DropColumn(
                name: "DevicePublicKeyPem",
                schema: "identity",
                table: "terminals");

            migrationBuilder.DropColumn(
                name: "EnrolledAt",
                schema: "identity",
                table: "terminals");
        }
    }
}
