using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovAcces.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scan_logs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitId = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AgentId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    WasGranted = table.Column<bool>(type: "boolean", nullable: false),
                    WasCheckOut = table.Column<bool>(type: "boolean", nullable: false),
                    IsSecurityEvent = table.Column<bool>(type: "boolean", nullable: false),
                    DenialReason = table.Column<int>(type: "integer", nullable: true),
                    RecordedInDegradedMode = table.Column<bool>(type: "boolean", nullable: false),
                    OverstayMinutes = table.Column<int>(type: "integer", nullable: true),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scan_logs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "visits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitToken = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    VisitorCompany = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    VisitorPhone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    VisitorEmail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Motif = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    HostUserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PlannedDurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    IsOnSite = table.Column<bool>(type: "boolean", nullable: false),
                    CheckedInAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CheckedOutAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HasCompletedCycle = table.Column<bool>(type: "boolean", nullable: false),
                    IsExcluded = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OverstayLevel = table.Column<int>(type: "integer", nullable: false),
                    LastOverstayAlertAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_scan_logs_IsSecurityEvent",
                table: "scan_logs",
                column: "IsSecurityEvent");

            migrationBuilder.CreateIndex(
                name: "IX_scan_logs_Timestamp",
                table: "scan_logs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_visits_HostUserId",
                table: "visits",
                column: "HostUserId");

            migrationBuilder.CreateIndex(
                name: "IX_visits_IsOnSite",
                table: "visits",
                column: "IsOnSite");

            migrationBuilder.CreateIndex(
                name: "IX_visits_VisitToken",
                table: "visits",
                column: "VisitToken",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scan_logs");

            migrationBuilder.DropTable(
                name: "visits");
        }
    }
}
