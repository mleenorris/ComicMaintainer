using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddComicVineVolumeNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ComicVineVolumeLinks_SeriesKey_StartYear",
                table: "ComicVineVolumeLinks");

            migrationBuilder.AddColumn<int>(
                name: "VolumeNumber",
                table: "ComicVineVolumeLinks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ComicVineVolumeLinks_SeriesKey_StartYear_VolumeNumber",
                table: "ComicVineVolumeLinks",
                columns: new[] { "SeriesKey", "StartYear", "VolumeNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ComicVineVolumeLinks_SeriesKey_StartYear_VolumeNumber",
                table: "ComicVineVolumeLinks");

            migrationBuilder.DropColumn(
                name: "VolumeNumber",
                table: "ComicVineVolumeLinks");

            migrationBuilder.CreateIndex(
                name: "IX_ComicVineVolumeLinks_SeriesKey_StartYear",
                table: "ComicVineVolumeLinks",
                columns: new[] { "SeriesKey", "StartYear" },
                unique: true);
        }
    }
}
