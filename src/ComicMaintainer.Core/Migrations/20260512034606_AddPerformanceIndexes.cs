using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ProcessingHistory_EntryId",
                table: "ProcessingHistory",
                column: "EntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingHistory_FilePath",
                table: "ProcessingHistory",
                column: "FilePath");

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_CreatedAt",
                table: "ComicFiles",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_Directory",
                table: "ComicFiles",
                column: "Directory");

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_UpdatedAt",
                table: "ComicFiles",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_ApiKey",
                table: "AspNetUsers",
                column: "ApiKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProcessingHistory_EntryId",
                table: "ProcessingHistory");

            migrationBuilder.DropIndex(
                name: "IX_ProcessingHistory_FilePath",
                table: "ProcessingHistory");

            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_CreatedAt",
                table: "ComicFiles");

            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_Directory",
                table: "ComicFiles");

            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_UpdatedAt",
                table: "ComicFiles");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_ApiKey",
                table: "AspNetUsers");
        }
    }
}
