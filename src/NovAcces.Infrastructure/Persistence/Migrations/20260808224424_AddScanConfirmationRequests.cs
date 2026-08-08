using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovAcces.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScanConfirmationRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scan_confirmation_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitId = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    CheckpointId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AgentId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RequestingTerminalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scan_confirmation_requests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_scan_confirmation_requests_RequestingTerminalId",
                table: "scan_confirmation_requests",
                column: "RequestingTerminalId");

            migrationBuilder.CreateIndex(
                name: "IX_scan_confirmation_requests_Status",
                table: "scan_confirmation_requests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_scan_confirmation_requests_VisitId",
                table: "scan_confirmation_requests",
                column: "VisitId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scan_confirmation_requests");
        }
    }
}
