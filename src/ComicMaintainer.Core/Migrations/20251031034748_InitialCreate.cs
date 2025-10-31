using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ComicFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Directory = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    LastModified = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsProcessed = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDuplicate = table.Column<bool>(type: "INTEGER", nullable: false),
                    Metadata_Series = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Metadata_Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Metadata_Issue = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Metadata_Volume = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Metadata_Publisher = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Metadata_Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Metadata_Summary = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Metadata_Authors = table.Column<string>(type: "TEXT", nullable: true),
                    Metadata_Tags = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComicFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProcessingHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessingHistory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_FilePath",
                table: "ComicFiles",
                column: "FilePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_IsDuplicate",
                table: "ComicFiles",
                column: "IsDuplicate");

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_IsProcessed",
                table: "ComicFiles",
                column: "IsProcessed");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingHistory_Success",
                table: "ProcessingHistory",
                column: "Success");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingHistory_Timestamp",
                table: "ProcessingHistory",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComicFiles");

            migrationBuilder.DropTable(
                name: "ProcessingHistory");
        }
    }
}
