using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovAcces.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddPosteEnrollmentTicket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "TerminalId",
                schema: "identity",
                table: "terminal_enrollment_tickets",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "PosteCheckpointId",
                schema: "identity",
                table: "terminal_enrollment_tickets",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PosteLabel",
                schema: "identity",
                table: "terminal_enrollment_tickets",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "PosteSiteIds",
                schema: "identity",
                table: "terminal_enrollment_tickets",
                type: "text[]",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PosteCheckpointId",
                schema: "identity",
                table: "terminal_enrollment_tickets");

            migrationBuilder.DropColumn(
                name: "PosteLabel",
                schema: "identity",
                table: "terminal_enrollment_tickets");

            migrationBuilder.DropColumn(
                name: "PosteSiteIds",
                schema: "identity",
                table: "terminal_enrollment_tickets");

            migrationBuilder.AlterColumn<Guid>(
                name: "TerminalId",
                schema: "identity",
                table: "terminal_enrollment_tickets",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
