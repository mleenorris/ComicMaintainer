using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesMetadataVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MetadataVersion",
                table: "SeriesMetadataCache",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "SeriesMetadataVersion",
                table: "ComicFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_SeriesMetadataVersion",
                table: "ComicFiles",
                column: "SeriesMetadataVersion");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_SeriesMetadataVersion",
                table: "ComicFiles");

            migrationBuilder.DropColumn(
                name: "MetadataVersion",
                table: "SeriesMetadataCache");

            migrationBuilder.DropColumn(
                name: "SeriesMetadataVersion",
                table: "ComicFiles");
        }
    }
}
