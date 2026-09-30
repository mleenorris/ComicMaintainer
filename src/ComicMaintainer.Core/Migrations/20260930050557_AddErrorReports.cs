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
                    Level = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ExceptionType = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    MessageTemplate = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    RenderedMessage = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    SourceContext = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    StackTrace = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: true),
                    AppVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OccurrenceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    GitHubIssueNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    GitHubIssueUrl = table.Column<string>(type: "TEXT", nullable: true),
                    LastReportedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IssueCreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
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
                name: "IX_ErrorReports_IssueCreatedAt",
                table: "ErrorReports",
                column: "IssueCreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorReports_LastSeenAt",
                table: "ErrorReports",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorReports_State",
                table: "ErrorReports",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ErrorReports");
        }
    }
}
