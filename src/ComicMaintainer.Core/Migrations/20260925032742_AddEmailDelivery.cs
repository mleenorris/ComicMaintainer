using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ComicEmailDeliveries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DeviceEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    DeliveryFormat = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComicEmailDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EreaderDevices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    EmailAddress = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    DeliveryFormat = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EreaderDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SeriesEmailSubscriptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NormalizedSeriesKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SeriesTitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeliveryFormat = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSentAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesEmailSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeriesEmailSubscriptions_EreaderDevices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "EreaderDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComicEmailDeliveries_CreatedAt",
                table: "ComicEmailDeliveries",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ComicEmailDeliveries_FilePath_DeviceId_Status",
                table: "ComicEmailDeliveries",
                columns: new[] { "FilePath", "DeviceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_EreaderDevices_EmailAddress",
                table: "EreaderDevices",
                column: "EmailAddress",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeriesEmailSubscriptions_DeviceId",
                table: "SeriesEmailSubscriptions",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_SeriesEmailSubscriptions_NormalizedSeriesKey_DeviceId",
                table: "SeriesEmailSubscriptions",
                columns: new[] { "NormalizedSeriesKey", "DeviceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComicEmailDeliveries");

            migrationBuilder.DropTable(
                name: "SeriesEmailSubscriptions");

            migrationBuilder.DropTable(
                name: "EreaderDevices");
        }
    }
}
