using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class V3_library_IndexesMatchQueries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProcessingHistory_FilePath",
                table: "ProcessingHistory");

            migrationBuilder.DropIndex(
                name: "IX_ProcessingHistory_Success",
                table: "ProcessingHistory");

            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_CreatedAt",
                table: "ComicFiles");

            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_Directory",
                table: "ComicFiles");

            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_IsDuplicate",
                table: "ComicFiles");

            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_IsNormalized",
                table: "ComicFiles");

            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_IsProcessed",
                table: "ComicFiles");

            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_IsRead",
                table: "ComicFiles");

            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_IsRenamed",
                table: "ComicFiles");

            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_UpdatedAt",
                table: "ComicFiles");

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_FileSize",
                table: "ComicFiles",
                column: "FileSize");

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_IsDuplicate",
                table: "ComicFiles",
                column: "IsDuplicate",
                filter: "\"IsDuplicate\"");

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_LastModified",
                table: "ComicFiles",
                column: "LastModified");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_FileSize",
                table: "ComicFiles");

            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_IsDuplicate",
                table: "ComicFiles");

            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_LastModified",
                table: "ComicFiles");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingHistory_FilePath",
                table: "ProcessingHistory",
                column: "FilePath");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingHistory_Success",
                table: "ProcessingHistory",
                column: "Success");

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_CreatedAt",
                table: "ComicFiles",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_Directory",
                table: "ComicFiles",
                column: "Directory");

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_IsDuplicate",
                table: "ComicFiles",
                column: "IsDuplicate");

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_IsNormalized",
                table: "ComicFiles",
                column: "IsNormalized");

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_IsProcessed",
                table: "ComicFiles",
                column: "IsProcessed");

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_IsRead",
                table: "ComicFiles",
                column: "IsRead");

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_IsRenamed",
                table: "ComicFiles",
                column: "IsRenamed");

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_UpdatedAt",
                table: "ComicFiles",
                column: "UpdatedAt");
        }
    }
}
