using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddReadStatusFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsRead",
                table: "ComicFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "FileReadStatuses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    IsRead = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastReadDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileReadStatuses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_IsRead",
                table: "ComicFiles",
                column: "IsRead");

            migrationBuilder.CreateIndex(
                name: "IX_FileReadStatuses_FilePath",
                table: "FileReadStatuses",
                column: "FilePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FileReadStatuses_IsRead",
                table: "FileReadStatuses",
                column: "IsRead");

            migrationBuilder.CreateIndex(
                name: "IX_FileReadStatuses_LastReadDate",
                table: "FileReadStatuses",
                column: "LastReadDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileReadStatuses");

            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_IsRead",
                table: "ComicFiles");

            migrationBuilder.DropColumn(
                name: "IsRead",
                table: "ComicFiles");
        }
    }
}
