using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddErrorReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ErrorReports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExceptionType = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    StackTrace = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    Origin = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    Area = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AppVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Platform = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    OccurrenceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LogExcerpt = table.Column<string>(type: "TEXT", maxLength: 32768, nullable: true),
                    LastUserAction = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    LastReportedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReportedIssueNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    ReportedIssueState = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorReports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ErrorReports_Fingerprint",
                table: "ErrorReports",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ErrorReports_LastReportedAt",
                table: "ErrorReports",
                column: "LastReportedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorReports_LastSeenAt",
                table: "ErrorReports",
                column: "LastSeenAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ErrorReports");
        }
    }
}
