using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovAcces.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Actor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_audit", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_audit_Timestamp",
                table: "admin_audit",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_audit");
        }
    }
}
