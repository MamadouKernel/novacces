using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovAcces.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddApplicationAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "application_audit",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Actor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Path = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: false),
                    SiteId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_audit", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_application_audit_Actor_Timestamp",
                schema: "identity",
                table: "application_audit",
                columns: new[] { "Actor", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_application_audit_Timestamp",
                schema: "identity",
                table: "application_audit",
                column: "Timestamp");
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION identity.prevent_application_audit_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$ BEGIN
                    RAISE EXCEPTION 'application_audit is append-only';
                END; $$;
                CREATE TRIGGER application_audit_append_only
                BEFORE UPDATE OR DELETE ON identity.application_audit
                FOR EACH ROW EXECUTE FUNCTION identity.prevent_application_audit_mutation();
                ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS application_audit_append_only ON identity.application_audit;
                DROP FUNCTION IF EXISTS identity.prevent_application_audit_mutation();
                ");
            migrationBuilder.DropTable(
                name: "application_audit",
                schema: "identity");
        }
    }
}
