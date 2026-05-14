using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesImageColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImageContentType",
                table: "SeriesMetadataCache",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ImageDownloadedUtc",
                table: "SeriesMetadataCache",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageStatus",
                table: "SeriesMetadataCache",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocalImageFile",
                table: "SeriesMetadataCache",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteImageUrl",
                table: "SeriesMetadataCache",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImageContentType",
                table: "SeriesMetadataCache");

            migrationBuilder.DropColumn(
                name: "ImageDownloadedUtc",
                table: "SeriesMetadataCache");

            migrationBuilder.DropColumn(
                name: "ImageStatus",
                table: "SeriesMetadataCache");

            migrationBuilder.DropColumn(
                name: "LocalImageFile",
                table: "SeriesMetadataCache");

            migrationBuilder.DropColumn(
                name: "RemoteImageUrl",
                table: "SeriesMetadataCache");
        }
    }
}
