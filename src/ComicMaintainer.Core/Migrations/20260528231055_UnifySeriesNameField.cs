using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class UnifySeriesNameField : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rename the old pinned-localized-title column to the new unified
            // SeriesName column, preserving any pinned values as the
            // user-selected name.
            migrationBuilder.RenameColumn(
                name: "PinnedLocalizedTitle",
                table: "SeriesMetadataCache",
                newName: "SeriesName");

            // Backfill: a user-canonical override used to win over the pin and
            // was displayed/written verbatim as the canonical title, so carry
            // it forward as the user-selected SeriesName (overwriting any pin).
            migrationBuilder.Sql(
                "UPDATE SeriesMetadataCache " +
                "SET SeriesName = CanonicalTitle " +
                "WHERE IsUserCanonical = 1 " +
                "AND CanonicalTitle IS NOT NULL AND TRIM(CanonicalTitle) <> '';");

            // Normalize empty-string pins to NULL (automatic resolution).
            migrationBuilder.Sql(
                "UPDATE SeriesMetadataCache " +
                "SET SeriesName = NULL " +
                "WHERE SeriesName IS NOT NULL AND TRIM(SeriesName) = '';");

            migrationBuilder.DropColumn(
                name: "IsUserCanonical",
                table: "SeriesMetadataCache");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SeriesName",
                table: "SeriesMetadataCache",
                newName: "PinnedLocalizedTitle");

            migrationBuilder.AddColumn<bool>(
                name: "IsUserCanonical",
                table: "SeriesMetadataCache",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
