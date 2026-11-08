using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <summary>
    /// Adds <c>LibraryScans</c>, the durable record of the directory the file watcher last
    /// scanned completely: an untracked file under it arrived after that scan, whereas under
    /// any other directory it may belong to a collection not yet added. Starts empty, so the
    /// first scan of an upgraded library only adds files, as earlier versions did.
    /// </summary>
    public partial class V3_pipeline_LibraryScans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LibraryScans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Directory = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryScans", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryScans_Directory",
                table: "LibraryScans",
                column: "Directory",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LibraryScans");
        }
    }
}
