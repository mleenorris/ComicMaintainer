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
            // This migration unifies the old "PinnedLocalizedTitle" column into a
            // new "SeriesName" column and drops the "IsUserCanonical" column.
            //
            // It is written entirely as a single-transaction table rebuild rather
            // than using RenameColumn + DropColumn. The EF SQLite provider would
            // otherwise translate DropColumn into a table rebuild that runs
            // `PRAGMA foreign_keys = 0;` outside a transaction, while RenameColumn
            // runs inside one. That mix lets the rename commit before the rebuild,
            // so an interruption (e.g. process restart) leaves the migration
            // partially applied: the column is already renamed but the migration
            // history row is never written, and the next startup fails with
            // "no such column: PinnedLocalizedTitle".
            //
            // Doing everything as ordinary SQL keeps all the work inside the
            // migration's transaction (atomic, fully rolled back on failure) and,
            // crucially, makes it self-healing: the data copy uses `SELECT *`
            // which matches columns positionally, so it works whether the source
            // title column is still named "PinnedLocalizedTitle" (clean state) or
            // has already been renamed to "SeriesName" (partially-applied state).
            // SeriesMetadataCache has no foreign keys, so no PRAGMA toggling is
            // required for the rebuild.

            // Remove any leftover scratch tables from a previously interrupted
            // attempt so the rebuild can run cleanly.
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"ef_temp_SeriesMetadataCache\";");
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"ef_unify_SeriesMetadataCache\";");

            // Create the rebuilt table with the same column order as the existing
            // table, but with the title column named "SeriesName". Keeping the
            // arity and order identical lets the positional `SELECT *` copy below
            // carry the title value across regardless of its current column name.
            // "IsUserCanonical" is retained here so the backfill can read it, then
            // dropped natively at the end.
            migrationBuilder.Sql(
                "CREATE TABLE \"ef_unify_SeriesMetadataCache\" (" +
                "\"NormalizedKey\" TEXT NOT NULL CONSTRAINT \"PK_SeriesMetadataCache\" PRIMARY KEY, " +
                "\"Aliases\" TEXT NOT NULL, " +
                "\"CanonicalTitle\" TEXT NOT NULL, " +
                "\"CreatedAt\" TEXT NOT NULL, " +
                "\"ImageContentType\" TEXT NULL, " +
                "\"ImageDownloadedUtc\" TEXT NULL, " +
                "\"ImageStatus\" TEXT NULL, " +
                "\"LastLookupUtc\" TEXT NULL, " +
                "\"LocalImageFile\" TEXT NULL, " +
                "\"LocalizedTitlesJson\" TEXT NULL, " +
                "\"LookupStatus\" TEXT NULL, " +
                "\"MetadataVersion\" INTEGER NOT NULL, " +
                "\"PreferredLanguage\" TEXT NULL, " +
                "\"RemoteImageUrl\" TEXT NULL, " +
                "\"SeriesName\" TEXT NULL, " +
                "\"Source\" TEXT NULL, " +
                "\"UpdatedAt\" TEXT NOT NULL, " +
                "\"UserAliases\" TEXT NOT NULL, " +
                "\"IsUserCanonical\" INTEGER NOT NULL DEFAULT 0);");

            // Positional copy: works whether column 15 is "PinnedLocalizedTitle"
            // or already "SeriesName".
            migrationBuilder.Sql(
                "INSERT INTO \"ef_unify_SeriesMetadataCache\" " +
                "SELECT * FROM \"SeriesMetadataCache\";");

            // Backfill: a user-canonical override used to win over the pin and
            // was displayed/written verbatim as the canonical title, so carry
            // it forward as the user-selected SeriesName (overwriting any pin).
            migrationBuilder.Sql(
                "UPDATE \"ef_unify_SeriesMetadataCache\" " +
                "SET \"SeriesName\" = \"CanonicalTitle\" " +
                "WHERE \"IsUserCanonical\" = 1 " +
                "AND \"CanonicalTitle\" IS NOT NULL AND TRIM(\"CanonicalTitle\") <> '';");

            // Normalize empty-string pins to NULL (automatic resolution).
            migrationBuilder.Sql(
                "UPDATE \"ef_unify_SeriesMetadataCache\" " +
                "SET \"SeriesName\" = NULL " +
                "WHERE \"SeriesName\" IS NOT NULL AND TRIM(\"SeriesName\") = '';");

            // Swap the rebuilt table into place and drop the obsolete column.
            migrationBuilder.Sql("DROP TABLE \"SeriesMetadataCache\";");
            migrationBuilder.Sql("ALTER TABLE \"ef_unify_SeriesMetadataCache\" RENAME TO \"SeriesMetadataCache\";");
            migrationBuilder.Sql("ALTER TABLE \"SeriesMetadataCache\" DROP COLUMN \"IsUserCanonical\";");
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
