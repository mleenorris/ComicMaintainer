using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddFileMetadataAuthoringFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastDbEditAt",
                table: "ComicFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastWriteAt",
                table: "ComicFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetadataSource",
                table: "ComicFiles",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Scanned");

            migrationBuilder.AddColumn<int>(
                name: "MetadataVersion",
                table: "ComicFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "Metadata_IsUserEdited",
                table: "ComicFiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Metadata_UserLockedFieldsMask",
                table: "ComicFiles",
                type: "INTEGER",
                nullable: true,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "WrittenMetadataVersion",
                table: "ComicFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ComicFiles_MetadataVersion_WrittenMetadataVersion",
                table: "ComicFiles",
                columns: new[] { "MetadataVersion", "WrittenMetadataVersion" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_MetadataVersion_WrittenMetadataVersion",
                table: "ComicFiles");

            migrationBuilder.DropColumn(
                name: "LastDbEditAt",
                table: "ComicFiles");

            migrationBuilder.DropColumn(
                name: "LastWriteAt",
                table: "ComicFiles");

            migrationBuilder.DropColumn(
                name: "MetadataSource",
                table: "ComicFiles");

            migrationBuilder.DropColumn(
                name: "MetadataVersion",
                table: "ComicFiles");

            migrationBuilder.DropColumn(
                name: "Metadata_IsUserEdited",
                table: "ComicFiles");

            migrationBuilder.DropColumn(
                name: "Metadata_UserLockedFieldsMask",
                table: "ComicFiles");

            migrationBuilder.DropColumn(
                name: "WrittenMetadataVersion",
                table: "ComicFiles");
        }
    }
}
