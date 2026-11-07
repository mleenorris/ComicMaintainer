using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <summary>
    /// Adds <c>ComicFiles.IsProcessingPending</c>, the watcher's durable record of the files
    /// it has queued for automatic processing, and queues the files flagged as duplicates once.
    /// </summary>
    public partial class V3_pipeline_ProcessingPending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsProcessingPending",
                table: "ComicFiles");
        }
    }
}
