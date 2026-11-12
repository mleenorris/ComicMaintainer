using System;
using ComicMaintainer.Core.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <summary>
    /// The schema and data changes of the 3.0.0 release, consolidated from the work of every
    /// package. The generated operations are edited by hand in two ways: <c>AspNetUsers.ApiKey</c>
    /// is dropped rather than renamed (the keys were plain text and never authenticated anyone,
    /// so users create a new one), and two data steps run alongside the schema changes they
    /// depend on.
    /// </summary>
    public partial class V3_0_0 : Migration
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
                name: "IX_EreaderDevices_EmailAddress",
                table: "EreaderDevices");

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

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_ApiKey",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ApiKey",
                table: "AspNetUsers");

            migrationBuilder.AddColumn<string>(
                name: "SourceId",
                table: "SeriesMetadataCache",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IssueCreationAttemptedAt",
                table: "ErrorReports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "EreaderDevices",
                type: "TEXT",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsProcessingPending",
                table: "ComicFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Earlier versions flagged a file as a duplicate when its new name differed from the
            // current one only in case on a case-insensitive file system (the file "collided"
            // with itself), and files directly in the library root collided under the root
            // folder's name. Queue every unprocessed duplicate for one more automatic pass, which
            // renames the wrongly flagged ones and clears their flag before "Delete All
            // Duplicates" can delete what is the only copy. Real duplicates are flagged again.
            migrationBuilder.Sql(
                "UPDATE \"ComicFiles\" SET \"IsProcessingPending\" = 1 " +
                "WHERE \"IsDuplicate\" = 1 AND \"IsProcessed\" = 0;");

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

            migrationBuilder.AddColumn<DateTime>(
                name: "MissingSince",
                table: "ComicFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "ComicEmailDeliveries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAt",
                table: "ComicEmailDeliveries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "ComicEmailDeliveries",
                type: "TEXT",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StartsWithoutOutcome",
                table: "ComicEmailDeliveries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApiKeyCreatedAt",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApiKeyLastUsedAt",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApiKeyHash",
                table: "AspNetUsers",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

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

            migrationBuilder.CreateTable(
                name: "RevokedTokens",
                columns: table => new
                {
                    TokenId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevokedTokens", x => x.TokenId);
                });

            // Devices and deliveries saved before they had owners go to the
            // first administrator (see EmailOwnershipBackfill).
            migrationBuilder.Sql(EmailOwnershipBackfill.Sql);

            migrationBuilder.CreateIndex(
                name: "IX_EreaderDevices_OwnerUserId_EmailAddress",
                table: "EreaderDevices",
                columns: new[] { "OwnerUserId", "EmailAddress" },
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_ComicEmailDeliveries_OwnerUserId_CreatedAt",
                table: "ComicEmailDeliveries",
                columns: new[] { "OwnerUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_ApiKeyHash",
                table: "AspNetUsers",
                column: "ApiKeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryScans_Directory",
                table: "LibraryScans",
                column: "Directory",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RevokedTokens_ExpiresAt",
                table: "RevokedTokens",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LibraryScans");

            migrationBuilder.DropTable(
                name: "RevokedTokens");

            migrationBuilder.DropIndex(
                name: "IX_EreaderDevices_OwnerUserId_EmailAddress",
                table: "EreaderDevices");

            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_FileSize",
                table: "ComicFiles");

            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_IsDuplicate",
                table: "ComicFiles");

            migrationBuilder.DropIndex(
                name: "IX_ComicFiles_LastModified",
                table: "ComicFiles");

            migrationBuilder.DropIndex(
                name: "IX_ComicEmailDeliveries_OwnerUserId_CreatedAt",
                table: "ComicEmailDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_ApiKeyHash",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "SeriesMetadataCache");

            migrationBuilder.DropColumn(
                name: "IssueCreationAttemptedAt",
                table: "ErrorReports");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "EreaderDevices");

            migrationBuilder.DropColumn(
                name: "IsProcessingPending",
                table: "ComicFiles");

            migrationBuilder.DropColumn(
                name: "MetadataScannedModified",
                table: "ComicFiles");

            migrationBuilder.DropColumn(
                name: "MetadataScannedSize",
                table: "ComicFiles");

            migrationBuilder.DropColumn(
                name: "MissingSince",
                table: "ComicFiles");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "ComicEmailDeliveries");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "ComicEmailDeliveries");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "ComicEmailDeliveries");

            migrationBuilder.DropColumn(
                name: "StartsWithoutOutcome",
                table: "ComicEmailDeliveries");

            migrationBuilder.DropColumn(
                name: "ApiKeyCreatedAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ApiKeyHash",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ApiKeyLastUsedAt",
                table: "AspNetUsers");

            migrationBuilder.AddColumn<string>(
                name: "ApiKey",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingHistory_FilePath",
                table: "ProcessingHistory",
                column: "FilePath");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingHistory_Success",
                table: "ProcessingHistory",
                column: "Success");

            migrationBuilder.CreateIndex(
                name: "IX_EreaderDevices_EmailAddress",
                table: "EreaderDevices",
                column: "EmailAddress",
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_ApiKey",
                table: "AspNetUsers",
                column: "ApiKey");
        }
    }
}
