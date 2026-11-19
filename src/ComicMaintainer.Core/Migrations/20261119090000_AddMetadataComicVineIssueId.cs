using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddMetadataComicVineIssueId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Metadata_ComicVineIssueId",
                table: "ComicFiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Metadata_ComicVineIssueIdSource",
                table: "ComicFiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RejectedTagId",
                table: "ComicVineFileStates",
                type: "INTEGER",
                nullable: true);

            // Every archive's tags are read again by the background scan, so the ComicVine issue
            // they name is stored for files scanned before this column existed.
            migrationBuilder.Sql("UPDATE \"ComicFiles\" SET \"MetadataScannedSize\" = NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RejectedTagId",
                table: "ComicVineFileStates");

            migrationBuilder.DropColumn(
                name: "Metadata_ComicVineIssueIdSource",
                table: "ComicFiles");

            migrationBuilder.DropColumn(
                name: "Metadata_ComicVineIssueId",
                table: "ComicFiles");
        }
    }
}
