using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddComicVineCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ComicVineFileStates",
                columns: table => new
                {
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    IssueId = table.Column<int>(type: "INTEGER", nullable: false),
                    AppliedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComicVineFileStates", x => x.FilePath);
                });

            migrationBuilder.CreateTable(
                name: "ComicVineIssues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    VolumeId = table.Column<int>(type: "INTEGER", nullable: false),
                    IssueNumber = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CoverDate = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    SiteUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Writers = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    StoryArcsJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ListedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DetailsUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComicVineIssues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ComicVineStoryArcs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    SiteUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    IssuesJson = table.Column<string>(type: "TEXT", nullable: false),
                    FetchedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComicVineStoryArcs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ComicVineVolumeLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeriesKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    StartYear = table.Column<int>(type: "INTEGER", nullable: false),
                    VolumeId = table.Column<int>(type: "INTEGER", nullable: true),
                    VolumeName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Publisher = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    LinkedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IssuesListedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComicVineVolumeLinks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComicVineFileStates_IssueId",
                table: "ComicVineFileStates",
                column: "IssueId");

            migrationBuilder.CreateIndex(
                name: "IX_ComicVineIssues_VolumeId",
                table: "ComicVineIssues",
                column: "VolumeId");

            migrationBuilder.CreateIndex(
                name: "IX_ComicVineVolumeLinks_SeriesKey_StartYear",
                table: "ComicVineVolumeLinks",
                columns: new[] { "SeriesKey", "StartYear" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComicVineVolumeLinks_VolumeId",
                table: "ComicVineVolumeLinks",
                column: "VolumeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComicVineFileStates");

            migrationBuilder.DropTable(
                name: "ComicVineIssues");

            migrationBuilder.DropTable(
                name: "ComicVineStoryArcs");

            migrationBuilder.DropTable(
                name: "ComicVineVolumeLinks");
        }
    }
}
