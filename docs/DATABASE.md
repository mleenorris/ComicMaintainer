# Database

ComicMaintainer keeps its state in one SQLite database. EF Core owns the schema.

## Where it is

The database is `comicmaintainer.db` in the config directory, so `/Config/comicmaintainer.db` by default.
`CONFIG_DIR` changes the directory. While the app runs, SQLite keeps `comicmaintainer.db-wal` and
`comicmaintainer.db-shm` beside it. Those three files are one database: never copy or replace only the main file while
the app is running.

The config directory also holds `user-settings.json`, the log files, `DataProtection-Keys/`, `backups/`,
`series-images/` and the generated `jwt-secret.key`.

To put the database somewhere else, set `ConnectionStrings__DefaultConnection` to a SQLite connection string, such as
`Data Source=/data/comicmaintainer.db`.

## How it is opened

Every connection is pooled and runs these settings. None of them are configurable.

| Setting | Value | Why |
| --- | --- | --- |
| `busy_timeout` | 5000 ms | Writers wait for each other instead of failing at once |
| `journal_mode` | WAL | Readers and a writer do not block each other |
| `synchronous` | NORMAL | Safe with WAL and much faster than FULL |
| `temp_store` | MEMORY | Temporary tables stay in memory |
| `cache_size` | 64 MiB | Page cache per connection |
| `mmap_size` | 256 MiB | Memory-mapped reads |

Set `EFCORE_QUERY_LOGGING=true` to print the SQL commands to the console. It is slow, so turn it off again afterwards.

## Tables

The model is in `src/ComicMaintainer.Core/Data/ComicMaintainerDbContext.cs`. Indexes below leave out primary keys.

| Table | Holds | Indexes |
| --- | --- | --- |
| `ComicFiles` | One row per tracked comic: path, size, date, status flags, the cached ComicInfo metadata (`Metadata_*` columns) and the bookkeeping below | `FilePath` (unique), `IsDuplicate` (partial, duplicates only), (`Directory`, `FileName`), `FileName`, `LastModified`, `FileSize`, `SeriesMetadataVersion`, (`MetadataVersion`, `WrittenMetadataVersion`) |
| `UserFileReadStatuses` | Read state and resume page, per user and file | (`UserId`, `FilePath`) unique, (`UserId`, `IsRead`), `FilePath` |
| `ProcessingHistory` | The history of file operations and their before and after values | `Timestamp`, `EntryId` (unique) |
| `ProcessingJobs` | Batch jobs, so a job outlives a restart as Interrupted | `JobId` (unique), `Status`, `StartTime` |
| `SeriesMetadataCache` | Provider matches, aliases, names, covers and series details, keyed by `NormalizedKey` | None besides the key |
| `ReadingProgresses` | Per-user reading progress | (`UserId`, `ContentId`) unique |
| `ReadingSessions` | Individual reading sessions | `SessionId` (unique), (`UserId`, `StartedAt`) |
| `ReaderPreferences`, `UserPreferences` | Per-user reader and web UI preferences | `UserId` (unique) on each |
| `ScheduledJobs` | Schedule, options and last result of each scheduled job | `JobKey` (unique) |
| `MetadataAuditFindings` | Findings of the audit jobs | `FindingType`, `FilePath` |
| `EreaderDevices` | Saved e-reader addresses, per owner | (`OwnerUserId`, `EmailAddress`) unique |
| `SeriesEmailSubscriptions` | Automatic per-series delivery; removed with its device | (`NormalizedSeriesKey`, `DeviceId`) unique |
| `ComicEmailDeliveries` | Email queue and history, with retry state | (`OwnerUserId`, `CreatedAt`), (`FilePath`, `DeviceId`, `Status`), `CreatedAt`, (`Status`, `SentAt`) |
| `NotificationDeliveries` | Durable queue of notifications and media-server rescans | (`Status`, `DueAt`), (`Target`, `EventType`, `GroupKey`, `Status`), `CreatedAt` |
| `ErrorReports` | One row per distinct failure for automated error reporting | `Fingerprint` (unique), `LastSeenAt`, `State`, `IssueCreatedAt` |
| `RevokedTokens` | Session tokens revoked before they expire | `ExpiresAt` |
| `LibraryScans` | The directory last scanned to the end; at most one row | `Directory` (unique) |
| `AspNetUsers` and the other `AspNet*` tables | ASP.NET Core Identity accounts and roles. API keys are stored as a hash in `AspNetUsers.ApiKeyHash`, unique when set | Identity's own, plus `ApiKeyHash` |
| `__EFMigrationsHistory` | Which migrations have been applied | None |

Notes on `ComicFiles`:

- `IsRead` is a leftover column that nothing reads. Read state is per user, in `UserFileReadStatuses`.
- `IsProcessingPending` marks files the watcher has queued, so a restart does not lose them.
- `MissingSince` is set when a cleanup first misses the file, and cleared when the file reappears.
- `MetadataScannedSize` and `MetadataScannedModified` say which version of the archive the cached metadata came from. A
  background scan fills the cache. An archive changed outside the app is read again only once the watcher or a library
  scan has refreshed its stored size and date.

## Migrations

Migrations are in `src/ComicMaintainer.Core/Migrations` and run automatically at every start, in this order:

1. A restore staged from the web interface replaces the database.
2. If an existing database has pending migrations, it is backed up as
   `backups/comicmaintainer-<timestamp>-premigrate.db`. If that backup fails, the app stops without changing the
   database. Free some disk space or fix the folder's permissions, and start it again.
3. The migrations run. A restart during an unfinished upgrade keeps the first backup instead of taking another.

Migrations cannot be undone, and older versions cannot open a migrated database. To go back to an older version, stop
the app and restore the `premigrate` backup. See [Restoring without the app](#restoring-without-the-app).

To add a migration, change the model and run this from the repository root:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/ComicMaintainer.Core --startup-project src/ComicMaintainer.WebApi
```

Commit the migration, its designer file and the updated `ComicMaintainerDbContextModelSnapshot.cs` together.

## Backups

| Backup | When | Kept |
| --- | --- | --- |
| `comicmaintainer-<timestamp>.db` | Daily by the `database-backup` scheduled job, and on request | The newest 7 (`AppSettings__DatabaseBackupRetention`) |
| `comicmaintainer-<timestamp>-premigrate.db` | Before pending migrations run | The newest 3 |
| `comicmaintainer-<timestamp>-prerestore.db` | When a restore is staged | Counted with the regular backups |

Backups are written to `backups/` in the config directory, or to `AppSettings__DatabaseBackupDirectory`. They are taken
with SQLite's `VACUUM INTO`, so each is a consistent copy made while the app keeps working, and compacted at the same
time.

Administrators manage backups at **Settings > Backups**, or through `/api/backups`: create, download, restore, delete,
and upload a backup to restore it. Backups contain password hashes and stored credentials, so keep them private.

### Restoring from the web interface

1. Open **Settings > Backups** and click **Restore** next to a backup.
2. The file is checked (SQLite header, `PRAGMA integrity_check`, a ComicMaintainer schema). A safety backup of the
   current database is taken, and the file is staged as `comicmaintainer.db.restore-pending`.
3. Restart the app. The staged file replaces the database, and any pending migrations then run. Until you restart,
   **Cancel pending restore** discards it.

### Restoring without the app

Use this when the app cannot start, or to return to an older version.

```bash
docker stop comicmaintainer-dotnet
cd /path/to/config        # the host folder mounted at /Config
cp backups/comicmaintainer-<timestamp>-premigrate.db comicmaintainer.db
rm -f comicmaintainer.db-wal comicmaintainer.db-shm
docker start comicmaintainer-dotnet
```

Remove the `-wal` and `-shm` files. They belong to the database you are replacing, and SQLite would otherwise apply them
to the restored one.

## Compacting the database

Deleted rows leave free space inside the file, and SQLite reuses it, so the file does not grow forever. The app never
runs `VACUUM`, and it is rarely worth doing. It only helps after a large deletion, such as removing most of a library,
when you want the disk space back.

`VACUUM` needs exclusive access to the database for as long as it runs, so it must not run while the app is using the
database. It also needs free disk space up to the size of the database.

### With a backup and restore (no extra tools)

A backup is already compacted, so restoring a fresh one does the same job.

1. Wait for running jobs to finish.
2. At **Settings > Backups**, click **Create backup now**. The new backup shows the compacted size.
3. Click **Restore** next to that backup, then restart the app.

Changes made between step 2 and the restart are lost. The safety backup from step 3 still has them.

### With sqlite3

The Docker image does not include `sqlite3`, so run it on the host, with the app stopped. Create a backup at
**Settings > Backups** first.

```bash
docker stop comicmaintainer-dotnet
cd /path/to/config                                      # the host folder mounted at /Config
ls -l comicmaintainer.db                                # must list the file (see below)
sqlite3 comicmaintainer.db "PRAGMA integrity_check;"    # must print: ok
sqlite3 comicmaintainer.db "VACUUM;"
docker start comicmaintainer-dotnet
```

Check the path first. Given a path that does not exist, `sqlite3` creates a new, empty database there and reports no
error. Replace `comicmaintainer-dotnet` if your container has another name. For the Windows download, stop the service
or program, and use the folder set in `CONFIG_DIR`.

## Scheduled maintenance

The cleanup runs every `DatabaseCleanupIntervalHours` (12 by default), and once two minutes after each start. Set the
interval to 0 at **Settings > Database Cleanup** to run it only after a start. Each run:

1. Removes stale rows, under the guards below.
2. Prunes processing history. Entries older than 90 days, or beyond the newest 50,000, are removed. Change this with
   `PROCESSING_HISTORY_RETENTION_DAYS` and `PROCESSING_HISTORY_MAX_ENTRIES`; 0 turns either limit off. Operations that
   changed nothing are never recorded.
3. Runs `PRAGMA optimize`, so SQLite's query planner has current statistics.

Finished batch jobs are kept for 7 days, and the newest 50 for longer.

### Stale entries and unmounted shares

Removing the row of a missing file also removes its read state, reader progress and metadata edits. A network share
that has dropped looks exactly like a deleted library, so a cleanup is careful about what "missing" means:

| Situation | What happens |
| --- | --- |
| The watched directory is missing or cannot be read | Nothing is removed. Confirming does not override this. |
| The sentinel file is set and missing | Nothing is removed. Confirming does not override this. |
| The watched directory holds no comics | Nothing is removed. A manual run asks for confirmation. |
| A file is missing for the first time | Its row is only stamped with `MissingSince`. A later run, at least `STALE_CLEANUP_MISSING_GRACE_HOURS` (6) after that stamp, removes it. Even 0 takes two runs. |
| A run would remove more than `STALE_CLEANUP_MAX_REMOVAL_PERCENT` (10) of the library, and more than 5 files | Nothing is removed. A manual run asks for confirmation. |
| A duplicate directory is unavailable, or holds no comics | Its rows are left alone. A manual run can confirm a directory that exists but is empty. |
| Checking a file fails with an I/O or permission error | That file is left alone. |

Scheduled runs never confirm. **Clean Database Now** at **Settings > Database Cleanup** asks for confirmation where the
table says so, and when missing files have not stayed missing for long enough. A confirmed run removes everything
missing at that moment. Scripts call `POST /api/settings/cleanup-database` or `POST /api/files/cleanup-stale`, handle
`409`, and repeat with `?confirm=true` to confirm.

If your library is on a network share or removable disk, set a sentinel file. Create a file on the share, such as
`.comicmaintainer-library`, and put that name in **Library Sentinel File** or `STALE_CLEANUP_SENTINEL_FILE`. A relative
path is resolved against the watched directory. While the share is unmounted, the file cannot be found and every cleanup
stops.
