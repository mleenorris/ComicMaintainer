using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddComicVineIssueCover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoverThumbUrl",
                table: "ComicVineIssues",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CoverUrl",
                table: "ComicVineIssues",
                type: "TEXT",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverThumbUrl",
                table: "ComicVineIssues");

            migrationBuilder.DropColumn(
                name: "CoverUrl",
                table: "ComicVineIssues");
        }
    }
}
