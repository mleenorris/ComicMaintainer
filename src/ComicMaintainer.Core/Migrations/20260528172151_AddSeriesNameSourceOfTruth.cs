using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesNameSourceOfTruth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SeriesName",
                table: "SeriesMetadataCache",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeriesNameLanguage",
                table: "SeriesMetadataCache",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeriesNameSource",
                table: "SeriesMetadataCache",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SeriesName",
                table: "SeriesMetadataCache");

            migrationBuilder.DropColumn(
                name: "SeriesNameLanguage",
                table: "SeriesMetadataCache");

            migrationBuilder.DropColumn(
                name: "SeriesNameSource",
                table: "SeriesMetadataCache");
        }
    }
}
