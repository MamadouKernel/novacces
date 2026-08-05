using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovAcces.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddTerminalActiveShift : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActiveShiftJti",
                schema: "identity",
                table: "terminals",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActiveShiftMatricule",
                schema: "identity",
                table: "terminals",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ActiveShiftStartedAt",
                schema: "identity",
                table: "terminals",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActiveShiftJti",
                schema: "identity",
                table: "terminals");

            migrationBuilder.DropColumn(
                name: "ActiveShiftMatricule",
                schema: "identity",
                table: "terminals");

            migrationBuilder.DropColumn(
                name: "ActiveShiftStartedAt",
                schema: "identity",
                table: "terminals");
        }
    }
}
