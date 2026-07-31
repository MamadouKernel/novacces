using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovAcces.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "refresh_sessions",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SiteId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_sessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_refresh_sessions_ExpiresAt",
                schema: "identity",
                table: "refresh_sessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_sessions_SubjectType_SubjectId",
                schema: "identity",
                table: "refresh_sessions",
                columns: new[] { "SubjectType", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_refresh_sessions_TokenHash",
                schema: "identity",
                table: "refresh_sessions",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refresh_sessions",
                schema: "identity");
        }
    }
}
