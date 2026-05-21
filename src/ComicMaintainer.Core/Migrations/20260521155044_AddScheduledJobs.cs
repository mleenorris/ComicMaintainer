using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MetadataAuditFindings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    FindingType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExpectedSeries = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ActualSeries = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ActualIssue = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Details = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    DetectedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetadataAuditFindings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    CronExpression = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LastRunUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextRunUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastDurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    LastStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LastMessage = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    OptionsJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetadataAuditFindings_FilePath",
                table: "MetadataAuditFindings",
                column: "FilePath");

            migrationBuilder.CreateIndex(
                name: "IX_MetadataAuditFindings_FindingType",
                table: "MetadataAuditFindings",
                column: "FindingType");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledJobs_JobKey",
                table: "ScheduledJobs",
                column: "JobKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetadataAuditFindings");

            migrationBuilder.DropTable(
                name: "ScheduledJobs");
        }
    }
}
