using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class V3W2_cleanup_RemoveFileReadStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileReadStatuses");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FileReadStatuses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CurrentPage = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    IsRead = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastReadDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileReadStatuses", x => x.Id);
                });

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
    }
}
