using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class V3_0_0_Features : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GenresJson",
                table: "SeriesMetadataCache",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "SeriesMetadataCache",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicationStatus",
                table: "SeriesMetadataCache",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SiteUrl",
                table: "SeriesMetadataCache",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TotalChapters",
                table: "SeriesMetadataCache",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NotificationDeliveries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Target = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    GroupKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DueAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDeliveries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_CreatedAt",
                table: "NotificationDeliveries",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_Status_DueAt",
                table: "NotificationDeliveries",
                columns: new[] { "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_Target_EventType_GroupKey_Status",
                table: "NotificationDeliveries",
                columns: new[] { "Target", "EventType", "GroupKey", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationDeliveries");

            migrationBuilder.DropColumn(
                name: "GenresJson",
                table: "SeriesMetadataCache");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "SeriesMetadataCache");

            migrationBuilder.DropColumn(
                name: "PublicationStatus",
                table: "SeriesMetadataCache");

            migrationBuilder.DropColumn(
                name: "SiteUrl",
                table: "SeriesMetadataCache");

            migrationBuilder.DropColumn(
                name: "TotalChapters",
                table: "SeriesMetadataCache");
        }
    }
}
