using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesMetadataCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SeriesMetadataCache",
                columns: table => new
                {
                    NormalizedKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    CanonicalTitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Aliases = table.Column<string>(type: "TEXT", nullable: false),
                    UserAliases = table.Column<string>(type: "TEXT", nullable: false),
                    IsUserCanonical = table.Column<bool>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LastLookupUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LookupStatus = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesMetadataCache", x => x.NormalizedKey);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeriesMetadataCache");
        }
    }
}
