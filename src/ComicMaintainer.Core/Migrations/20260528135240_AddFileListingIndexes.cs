using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddFileListingIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_Directory_FileName",
                table: "ComicFiles",
                columns: new[] { "Directory", "FileName" });

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_FileName",
                table: "ComicFiles",
                column: "FileName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_Directory_FileName",
                table: "ComicFiles");

            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_FileName",
                table: "ComicFiles");
        }
    }
}
