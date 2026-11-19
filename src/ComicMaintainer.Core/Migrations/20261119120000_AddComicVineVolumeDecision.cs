using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddComicVineVolumeDecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CandidatesJson",
                table: "ComicVineVolumeLinks",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Decision",
                table: "ComicVineVolumeLinks",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewReason",
                table: "ComicVineVolumeLinks",
                type: "TEXT",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CandidatesJson",
                table: "ComicVineVolumeLinks");

            migrationBuilder.DropColumn(
                name: "Decision",
                table: "ComicVineVolumeLinks");

            migrationBuilder.DropColumn(
                name: "ReviewReason",
                table: "ComicVineVolumeLinks");
        }
    }
}
