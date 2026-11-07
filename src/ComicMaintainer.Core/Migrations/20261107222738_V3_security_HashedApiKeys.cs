using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <summary>
    /// Stores API keys as SHA-256 hashes, with when they were created and last used. Keys were stored in plain text
    /// before, but no request was ever authenticated with one, so they are dropped rather than converted: users
    /// create a new key.
    /// </summary>
    public partial class V3_security_HashedApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_ApiKey",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ApiKey",
                table: "AspNetUsers");

            migrationBuilder.AddColumn<string>(
                name: "ApiKeyHash",
                table: "AspNetUsers",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApiKeyCreatedAt",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApiKeyLastUsedAt",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_ApiKeyHash",
                table: "AspNetUsers",
                column: "ApiKeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_ApiKeyHash",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ApiKeyLastUsedAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ApiKeyCreatedAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ApiKeyHash",
                table: "AspNetUsers");

            migrationBuilder.AddColumn<string>(
                name: "ApiKey",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_ApiKey",
                table: "AspNetUsers",
                column: "ApiKey");
        }
    }
}
