using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class V3W3_enrich_ProviderSeriesDetails : Migration
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
