using ComicMaintainer.Core.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class V3_delivery_EreaderDeviceOwners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EreaderDevices_EmailAddress",
                table: "EreaderDevices");

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "EreaderDevices",
                type: "TEXT",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "ComicEmailDeliveries",
                type: "TEXT",
                maxLength: 450,
                nullable: true);

            // Devices and deliveries saved before they had owners go to the
            // first administrator (see EmailOwnershipBackfill).
            migrationBuilder.Sql(EmailOwnershipBackfill.Sql);

            migrationBuilder.CreateIndex(
                name: "IX_EreaderDevices_OwnerUserId_EmailAddress",
                table: "EreaderDevices",
                columns: new[] { "OwnerUserId", "EmailAddress" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComicEmailDeliveries_OwnerUserId_CreatedAt",
                table: "ComicEmailDeliveries",
                columns: new[] { "OwnerUserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EreaderDevices_OwnerUserId_EmailAddress",
                table: "EreaderDevices");

            migrationBuilder.DropIndex(
                name: "IX_ComicEmailDeliveries_OwnerUserId_CreatedAt",
                table: "ComicEmailDeliveries");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "EreaderDevices");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "ComicEmailDeliveries");

            migrationBuilder.CreateIndex(
                name: "IX_EreaderDevices_EmailAddress",
                table: "EreaderDevices",
                column: "EmailAddress",
                unique: true);
        }
    }
}
