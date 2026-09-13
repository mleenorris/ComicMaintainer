using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddPerUserFileReadStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserFileReadStatuses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    IsRead = table.Column<bool>(type: "INTEGER", nullable: false),
                    CurrentPage = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    LastReadDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserFileReadStatuses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserFileReadStatuses_FilePath",
                table: "UserFileReadStatuses",
                column: "FilePath");

            migrationBuilder.CreateIndex(
                name: "IX_UserFileReadStatuses_UserId_FilePath",
                table: "UserFileReadStatuses",
                columns: new[] { "UserId", "FilePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserFileReadStatuses_UserId_IsRead",
                table: "UserFileReadStatuses",
                columns: new[] { "UserId", "IsRead" });

            // ── Backfill ────────────────────────────────────────────────────────────
            //
            // Read state previously lived in two global places: ComicFiles.IsRead and the
            // FileReadStatuses table. Neither records *who* read the file, so the only
            // non-destructive migration is to attribute that history to a single account.
            // We pick an Admin if one exists (the account that performed setup, and in
            // practice the only account in a single-user deployment), otherwise any user.
            // If there are no users at all — a fresh install migrating before setup runs —
            // there is no history worth preserving and both statements insert nothing.
            //
            // Step 1: legacy global state → the owner account. FileReadStatuses supplies
            // the resume page; ComicFiles.IsRead supplies the read flag. A file may appear
            // in either or both, so the two are merged per path.
            migrationBuilder.Sql(@"
                INSERT INTO UserFileReadStatuses
                    (UserId, FilePath, IsRead, CurrentPage, LastReadDate, CreatedAt, UpdatedAt)
                SELECT
                    owner.Id,
                    src.FilePath,
                    src.IsRead,
                    src.CurrentPage,
                    src.LastReadDate,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM (
                    SELECT u.Id AS Id
                    FROM AspNetUsers u
                    LEFT JOIN AspNetUserRoles ur ON ur.UserId = u.Id
                    LEFT JOIN AspNetRoles r ON r.Id = ur.RoleId
                    ORDER BY CASE WHEN r.NormalizedName = 'ADMIN' THEN 0 ELSE 1 END, u.Id
                    LIMIT 1
                ) AS owner
                CROSS JOIN (
                    SELECT
                        cf.FilePath AS FilePath,
                        MAX(cf.IsRead, COALESCE(frs.IsRead, 0)) AS IsRead,
                        COALESCE(frs.CurrentPage, 1) AS CurrentPage,
                        frs.LastReadDate AS LastReadDate
                    FROM ComicFiles cf
                    LEFT JOIN FileReadStatuses frs ON frs.FilePath = cf.FilePath
                    WHERE cf.IsRead = 1 OR frs.FilePath IS NOT NULL

                    UNION ALL

                    -- Orphaned progress for files no longer tracked in ComicFiles; preserved
                    -- so re-adding the file restores the resume position.
                    SELECT
                        frs.FilePath,
                        frs.IsRead,
                        frs.CurrentPage,
                        frs.LastReadDate
                    FROM FileReadStatuses frs
                    WHERE NOT EXISTS (
                        SELECT 1 FROM ComicFiles cf WHERE cf.FilePath = frs.FilePath
                    )
                ) AS src;
            ");

            // Step 2: reconcile with the per-user reader progress that already existed.
            // ReadingProgresses is genuinely per-user, so it takes precedence over the
            // guessed attribution above: a completed record means that user has read the
            // file, and its page is a better resume position than the global one.
            migrationBuilder.Sql(@"
                INSERT INTO UserFileReadStatuses
                    (UserId, FilePath, IsRead, CurrentPage, LastReadDate, CreatedAt, UpdatedAt)
                SELECT
                    rp.UserId,
                    rp.ContentId,
                    CASE
                        WHEN rp.CompletedAt IS NOT NULL OR rp.PercentComplete >= 100 THEN 1
                        ELSE 0
                    END,
                    MAX(rp.CurrentPage, 1),
                    rp.LastReadAt,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM ReadingProgresses rp
                WHERE rp.UserId <> '' AND rp.ContentId <> ''
                ON CONFLICT(UserId, FilePath) DO UPDATE SET
                    IsRead = excluded.IsRead,
                    CurrentPage = excluded.CurrentPage,
                    LastReadDate = COALESCE(excluded.LastReadDate, UserFileReadStatuses.LastReadDate),
                    UpdatedAt = CURRENT_TIMESTAMP;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserFileReadStatuses");
        }
    }
}
