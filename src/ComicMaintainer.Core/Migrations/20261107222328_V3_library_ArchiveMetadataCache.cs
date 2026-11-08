using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class V3_library_ArchiveMetadataCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "MetadataScannedModified",
                table: "ComicFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MetadataScannedSize",
                table: "ComicFiles",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MetadataScannedModified",
                table: "ComicFiles");

            migrationBuilder.DropColumn(
                name: "MetadataScannedSize",
                table: "ComicFiles");
        }
    }
}
