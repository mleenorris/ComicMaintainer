using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

public partial class FileStoreService
{
    /// <summary>
    /// Removals one cleanup may always make, whatever
    /// <see cref="AppSettings.StaleCleanupMaxRemovalPercent"/> allows, so a small library
    /// can still be tidied without confirmation.
    /// </summary>
    public const int StaleCleanupAlwaysAllowedRemovals = 5;

    private enum LibraryRootState
    {
        /// <summary>The root does not exist or cannot be read.</summary>
        Unavailable,

        /// <summary>The root can be read but holds no comic archive at all.</summary>
        Empty,

        Populated
    }

    private enum TrackedFileState
    {
        Present,
        Missing,

        /// <summary>The check itself failed (I/O or permission error), so nothing is known.</summary>
        Unknown
    }

    /// <inheritdoc />
    /// <remarks>
    /// "Not visible right now" is not treated as "deleted". The run stops without changing
    /// anything when the watched directory is missing or unreadable, when the configured
    /// sentinel file is missing, or (unless confirmed) when the watched directory holds no
    /// comics at all — what an unmounted share looks like. A file must be missing on two
    /// runs at least <see cref="AppSettings.StaleCleanupMissingGraceHours"/> apart before a
    /// scheduled run deletes its row: the first run only stamps <c>MissingSince</c>, and the
    /// stamp is cleared when the file is seen again. A run that would delete more than
    /// <see cref="AppSettings.StaleCleanupMaxRemovalPercent"/> of the library deletes
    /// nothing unless an administrator confirms it. A file whose check fails with an I/O or
    /// permission error is left alone.
    /// </remarks>
    public async Task<StaleCleanupResult> CleanupStaleEntriesAsync(
        StaleCleanupOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = _settings.CurrentValue;
        var maxRemovalPercent = Math.Clamp(settings.StaleCleanupMaxRemovalPercent, 0, AppSettings.MaxStaleCleanupRemovalPercent);
        var grace = TimeSpan.FromHours(Math.Clamp(settings.StaleCleanupMissingGraceHours, 0, AppSettings.MaxStaleCleanupMissingGraceHours));
        var runKind = options.IsManual ? "manual" : "scheduled";
        var confirmed = options.IsManual && options.Confirmed;

        StaleCleanupResult Stop(StaleCleanupStatus status, string message, int tracked = 0)
        {
            _logger.LogWarning("Stale-entry cleanup ({RunKind}) skipped: {Reason}", runKind, message);
            return new StaleCleanupResult
            {
                Status = status,
                TrackedCount = tracked,
                MaxRemovalPercent = maxRemovalPercent,
                Message = message,
            };
        }

        try
        {
            _logger.LogInformation("Starting {RunKind} cleanup of stale database entries", runKind);

            var watchedDirectory = settings.WatchedDirectory;
            var rootState = ProbeLibraryRoot(watchedDirectory);
            if (rootState == LibraryRootState.Unavailable)
            {
                return Stop(
                    StaleCleanupStatus.WatchedDirectoryUnavailable,
                    $"the watched directory '{LoggingHelper.SanitizePathForLog(watchedDirectory)}' does not exist or cannot be read, so the library is presumed unmounted. No rows were removed.");
            }

            var sentinel = ResolveSentinelPath(settings);
            if (sentinel is not null && !File.Exists(sentinel))
            {
                return Stop(
                    StaleCleanupStatus.SentinelMissing,
                    $"the sentinel file '{LoggingHelper.SanitizePathForLog(sentinel)}' is missing, so the library is presumed unmounted. No rows were removed.");
            }

            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var rows = await dbContext.ComicFiles
                .AsNoTracking()
                .Select(e => new TrackedRow(e.FilePath, e.MissingSince))
                .ToListAsync(cancellationToken);
            if (rows.Count == 0)
            {
                _logger.LogInformation("No tracked files, nothing to clean up");
                return new StaleCleanupResult
                {
                    Status = StaleCleanupStatus.Completed,
                    MaxRemovalPercent = maxRemovalPercent,
                    Message = "No tracked files.",
                };
            }

            if (rootState == LibraryRootState.Empty && !confirmed)
            {
                return Stop(
                    StaleCleanupStatus.WatchedDirectoryEmpty,
                    $"the watched directory '{LoggingHelper.SanitizePathForLog(watchedDirectory)}' contains no comics although {rows.Count} file(s) are tracked, which is what an unmounted share looks like. No rows were removed.",
                    rows.Count);
            }

            // Rows in a duplicate directory that is not there (an unmounted share of its
            // own) cannot be checked either.
            var duplicateDirectory = settings.DuplicateDirectory;
            var duplicateRootMissing = !string.IsNullOrWhiteSpace(duplicateDirectory)
                && ProbeLibraryRoot(duplicateDirectory) == LibraryRootState.Unavailable;

            var now = DateTime.UtcNow;
            var removable = new List<string>();
            var newlyMissing = new List<string>();
            var restored = new List<string>();
            int missing = 0, unknown = 0;
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var state = duplicateRootMissing && IsWithin(row.FilePath, duplicateDirectory!)
                    ? TrackedFileState.Unknown
                    : ProbeTrackedFile(row.FilePath);
                switch (state)
                {
                    case TrackedFileState.Present:
                        if (row.MissingSince is not null)
                        {
                            restored.Add(row.FilePath);
                        }
                        break;
                    case TrackedFileState.Unknown:
                        unknown++;
                        break;
                    default:
                        missing++;
                        if (row.MissingSince is null)
                        {
                            newlyMissing.Add(row.FilePath);
                        }

                        // A manual run acts on what is missing now; a scheduled run only once
                        // the file has stayed missing for the grace period since an earlier run
                        // first missed it.
                        if (options.IsManual || (row.MissingSince is { } since && now - since >= grace))
                        {
                            removable.Add(row.FilePath);
                        }
                        break;
                }
            }

            var allowed = maxRemovalPercent >= AppSettings.MaxStaleCleanupRemovalPercent
                ? int.MaxValue
                : Math.Max(StaleCleanupAlwaysAllowedRemovals, (int)((long)rows.Count * maxRemovalPercent / 100));
            var overThreshold = removable.Count > allowed && !confirmed;
            var toDelete = overThreshold ? new List<string>() : removable;

            // Stamps, cleared stamps and deletions are saved together in one transaction. The
            // statements are set-based and keyed by path, so a row renamed while the disk was
            // being checked (it now has a different path) is left alone. A scheduled run only
            // deletes rows that still carry their stamp: re-adding a file that came back in
            // the meantime clears it (RefreshFromDisk), which keeps that row.
            cancellationToken.ThrowIfCancellationRequested();
            var deleteUnstamped = options.IsManual;
            var removedCount = 0;
            await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
            {
                var deleteSet = new HashSet<string>(toDelete, StringComparer.Ordinal);
                foreach (var chunk in newlyMissing.Where(path => !deleteSet.Contains(path)).Chunk(SqliteParameterChunkSize))
                {
                    await dbContext.ComicFiles
                        .Where(e => chunk.Contains(e.FilePath) && e.MissingSince == null)
                        .ExecuteUpdateAsync(set => set.SetProperty(e => e.MissingSince, now), cancellationToken);
                }

                foreach (var chunk in restored.Chunk(SqliteParameterChunkSize))
                {
                    await dbContext.ComicFiles
                        .Where(e => chunk.Contains(e.FilePath))
                        .ExecuteUpdateAsync(set => set.SetProperty(e => e.MissingSince, (DateTime?)null), cancellationToken);
                }

                foreach (var chunk in toDelete.Chunk(SqliteParameterChunkSize))
                {
                    removedCount += await dbContext.ComicFiles
                        .Where(e => chunk.Contains(e.FilePath) && (deleteUnstamped || e.MissingSince != null))
                        .ExecuteDeleteAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }

            if (toDelete.Count > 0)
            {
                foreach (var path in toDelete)
                {
                    ForgetCommittedRow(path);
                }
                MarkChanged();
                await BroadcastFileListUpdateAfterCleanupAsync();
            }

            var result = new StaleCleanupResult
            {
                Status = overThreshold ? StaleCleanupStatus.ThresholdExceeded : StaleCleanupStatus.Completed,
                RemovedCount = removedCount,
                TrackedCount = rows.Count,
                MissingCount = missing,
                RemovableCount = removable.Count,
                NewlyMissingCount = newlyMissing.Count,
                RestoredCount = restored.Count,
                UncheckedCount = unknown,
                MaxRemovalPercent = maxRemovalPercent,
                Message = overThreshold
                    ? $"{removable.Count} of {rows.Count} tracked files are missing, more than the {maxRemovalPercent}% one cleanup may remove without confirmation. No rows were removed; check that the whole library is mounted."
                    : DescribeCompletedCleanup(removedCount, missing - removable.Count, restored.Count, unknown, grace),
            };

            if (overThreshold)
            {
                _logger.LogWarning(
                    "Stale-entry cleanup ({RunKind}) skipped: {Removable} of {Tracked} tracked files are missing, more than the {Percent}% one cleanup may remove without confirmation. No rows were removed",
                    runKind, removable.Count, rows.Count, maxRemovalPercent);
            }
            else
            {
                _logger.LogInformation(
                    "Stale-entry cleanup ({RunKind}) completed: removed {Removed}, waiting {Waiting}, reappeared {Restored}, unchecked {Unchecked} of {Tracked} tracked files",
                    runKind, removedCount, missing - removable.Count, restored.Count, unknown, rows.Count);
            }

            if (unknown > 0)
            {
                _logger.LogWarning(
                    "Stale-entry cleanup could not check {Count} tracked file(s) because of I/O or permission errors; they were left alone",
                    unknown);
            }

            return result;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Cleanup of stale entries was cancelled; no rows were removed");
            return new StaleCleanupResult
            {
                Status = StaleCleanupStatus.Cancelled,
                MaxRemovalPercent = maxRemovalPercent,
                Message = "The cleanup was cancelled. No rows were removed.",
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error during cleanup of stale database entries");
            return new StaleCleanupResult
            {
                Status = StaleCleanupStatus.Failed,
                MaxRemovalPercent = maxRemovalPercent,
                Message = "The cleanup failed. No rows were removed.",
            };
        }
    }

    /// <summary>The columns of a tracked row the cleanup needs.</summary>
    private sealed record TrackedRow(string FilePath, DateTime? MissingSince);

    private async Task BroadcastFileListUpdateAfterCleanupAsync()
    {
        if (_eventBroadcaster is null)
        {
            return;
        }

        try
        {
            await _eventBroadcaster.BroadcastFileListUpdateAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Failed to broadcast file list update after stale-entry cleanup");
        }
    }

    private static string DescribeCompletedCleanup(int removed, int waiting, int restored, int unknown, TimeSpan grace)
    {
        var parts = new List<string> { $"Removed {removed} stale database entr{(removed == 1 ? "y" : "ies")}." };
        if (waiting > 0)
        {
            parts.Add($"{waiting} missing file(s) will be removed by a later cleanup if they are still missing {grace.TotalHours:0.#} hour(s) after they were first missed.");
        }
        if (restored > 0)
        {
            parts.Add($"{restored} file(s) reappeared.");
        }
        if (unknown > 0)
        {
            parts.Add($"{unknown} file(s) could not be checked and were left alone.");
        }
        return string.Join(' ', parts);
    }

    /// <summary>
    /// The sentinel file path from the settings (relative paths are relative to the watched
    /// directory), or <c>null</c> when no sentinel is configured.
    /// </summary>
    private static string? ResolveSentinelPath(AppSettings settings)
    {
        var sentinel = settings.StaleCleanupSentinelFile?.Trim();
        if (string.IsNullOrEmpty(sentinel))
        {
            return null;
        }

        return Path.IsPathRooted(sentinel) || string.IsNullOrWhiteSpace(settings.WatchedDirectory)
            ? sentinel
            : Path.Join(settings.WatchedDirectory, sentinel);
    }

    private static bool IsWithin(string path, string directory)
    {
        try
        {
            return LibraryPathValidator.IsLexicallyWithin(path, directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a library root can be read and holds at least one comic archive. Stops at the
    /// first archive, so a populated library is answered almost immediately.
    /// </summary>
    private static LibraryRootState ProbeLibraryRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return LibraryRootState.Unavailable;
        }

        try
        {
            if (!Directory.Exists(root))
            {
                return LibraryRootState.Unavailable;
            }

            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.None,
            };
            return Directory.EnumerateFiles(root, "*", enumeration).Any(ComicFileExtensions.IsComicArchive)
                ? LibraryRootState.Populated
                : LibraryRootState.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return LibraryRootState.Unavailable;
        }
    }

    /// <summary>
    /// Whether a tracked file is on disk. Unlike <see cref="File.Exists"/>, which also answers
    /// <c>false</c> for a stale NFS handle or a permission error, only "not found" counts as
    /// missing; any other failure is reported as unknown.
    /// </summary>
    private static TrackedFileState ProbeTrackedFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory) ? TrackedFileState.Missing : TrackedFileState.Present;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return TrackedFileState.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return TrackedFileState.Unknown;
        }
    }
}
