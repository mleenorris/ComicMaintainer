using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddRenamedAndNormalizedFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsNormalized",
                table: "ComicFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsRenamed",
                table: "ComicFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_IsNormalized",
                table: "ComicFiles",
                column: "IsNormalized");

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_IsRenamed",
                table: "ComicFiles",
                column: "IsRenamed");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_IsNormalized",
                table: "ComicFiles");

            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_IsRenamed",
                table: "ComicFiles");

            migrationBuilder.DropColumn(
                name: "IsNormalized",
                table: "ComicFiles");

            migrationBuilder.DropColumn(
                name: "IsRenamed",
                table: "ComicFiles");
        }
    }
}
