using ComicMaintainer.Core.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Backs the SQLite database up before startup applies pending EF Core migrations. Upgrades (often automatic, by
/// pulling <c>:dotnet</c>) migrate the database as soon as the new version starts, and the previous version cannot
/// open a migrated database, so without this backup going back to it would mean losing everything since the last
/// scheduled backup, or everything when scheduled backups are off.
/// </summary>
public static class PreMigrationBackup
{
    /// <summary>
    /// Backs the database up when it already exists and migrations are pending. A new database (no migrations
    /// applied yet) holds nothing to keep, so it is not backed up.
    /// </summary>
    /// <returns>The backup taken, or null when none was needed.</returns>
    /// <exception cref="InvalidOperationException">
    /// The backup failed. The migrations are then not applied, so the database still works with the version that
    /// last used it.
    /// </exception>
    public static async Task<DatabaseBackupInfo?> CreateIfMigrationsPendingAsync(
        DatabaseFacade database,
        string databasePath,
        IDatabaseBackupService? backups,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (backups is null || !File.Exists(databasePath))
        {
            return null;
        }

        var applied = (await database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
        if (applied.Count == 0)
        {
            return null;
        }

        var pending = (await database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
        {
            return null;
        }

        DatabaseBackupInfo backup;
        try
        {
            backup = await backups.CreatePreMigrationBackupAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
        {
            logger.LogCritical(
                ex,
                "Could not back up the database to {BackupDirectory} before applying {PendingCount} database migration(s), so they were not applied and ComicMaintainer is stopping. The database is unchanged and still works with the previous version. Free disk space or make the backup directory writable, then start again.",
                backups.BackupDirectory,
                pending.Count);
            throw new InvalidOperationException(
                $"Could not back up the database to {backups.BackupDirectory} before applying {pending.Count} database migration(s).", ex);
        }

        logger.LogInformation(
            "Backed up the database to {BackupPath} before applying {PendingCount} database migration(s) ({PendingMigrations}). The database was last migrated to {LastAppliedMigration}; to go back to the version that used it, restore this backup from Settings > Backups or copy it over {DatabaseFile} while ComicMaintainer is stopped.",
            Path.Join(backups.BackupDirectory, backup.Name),
            pending.Count,
            string.Join(", ", pending),
            applied[^1],
            Path.GetFileName(databasePath));
        return backup;
    }
}
