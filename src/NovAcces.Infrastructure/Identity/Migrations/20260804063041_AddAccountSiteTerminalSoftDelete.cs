using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovAcces.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountSiteTerminalSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_terminals_DeviceInstanceId",
                schema: "identity",
                table: "terminals");

            migrationBuilder.DropIndex(
                name: "UserNameIndex",
                schema: "identity",
                table: "AspNetUsers");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                schema: "identity",
                table: "terminals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                schema: "identity",
                table: "terminals",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                schema: "identity",
                table: "sites",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                schema: "identity",
                table: "sites",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                schema: "identity",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                schema: "identity",
                table: "AspNetUsers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_terminals_DeviceInstanceId",
                schema: "identity",
                table: "terminals",
                column: "DeviceInstanceId",
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                schema: "identity",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true,
                filter: "\"DeletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_terminals_DeviceInstanceId",
                schema: "identity",
                table: "terminals");

            migrationBuilder.DropIndex(
                name: "UserNameIndex",
                schema: "identity",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "identity",
                table: "terminals");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "identity",
                table: "terminals");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "identity",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "identity",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "identity",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "identity",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "IX_terminals_DeviceInstanceId",
                schema: "identity",
                table: "terminals",
                column: "DeviceInstanceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                schema: "identity",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);
        }
    }
}
