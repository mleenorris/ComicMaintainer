using System.Text.RegularExpressions;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpCompress.Archives;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// What a startup sweep for interrupted archive writes did.
/// </summary>
/// <param name="TempFilesDeleted">Unfinished rewritten archives that were deleted.</param>
/// <param name="BackupsRestored">Archives that were missing or unreadable and were put back from their backup.</param>
/// <param name="BackupsRemoved">Leftover backups of archives that are intact, which were deleted.</param>
/// <param name="BackupsKept">Backups left in place because neither they nor their archive could be read.</param>
public sealed record ArchiveWriteRecoveryResult(int TempFilesDeleted, int BackupsRestored, int BackupsRemoved, int BackupsKept)
{
    public bool DidAnything => TempFilesDeleted + BackupsRestored + BackupsRemoved + BackupsKept > 0;
}

/// <summary>
/// Cleans up after archive writes that a crash, an OOM kill or a container stop interrupted.
/// </summary>
/// <remarks>
/// <para>A metadata write builds the new archive next to the original under a name that the file
/// watcher, the library scan and this sweep recognise (<see cref="CreateTempPath"/>) and then
/// renames it over the original, so an interrupted write leaves the original intact plus an
/// unfinished temp file. The sweep deletes those temp files.</para>
/// <para>Versions before 3.0 instead wrote the temp archive to <c>TempFileDirectory</c> and, when that
/// was on another mount (the default Docker layout), copied the original to
/// <c>&lt;archive&gt;.backup</c> and overwrote the original in place. A stop during that copy left a
/// truncated archive with the good copy in the backup. For every such backup the sweep keeps the
/// archive when it can be read and removes the redundant backup; restores the backup when the
/// archive is missing or unreadable and the backup can be read; and otherwise leaves both alone
/// and logs an error. It also deletes the old version's orphaned temp archives.</para>
/// <para>Only files last written before the sweep started are touched, so it can run while new
/// writes are already happening.</para>
/// </remarks>
public sealed class ArchiveWriteRecoveryService
{
    /// <summary>Prefix the file watcher and the library scan already ignore.</summary>
    private const string TempPrefix = ".tmp_";

    /// <summary>Extension of an archive being rewritten; not a comic extension, so nothing opens it.</summary>
    private const string TempExtension = ".comicinfo-write";

    private const string BackupExtension = ".backup";

    private static readonly Regex TempNamePattern = new(
        @"^\.tmp_[0-9a-f]{32}\.comicinfo-write$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>The temp archives the old write path left in <c>TempFileDirectory</c>.</summary>
    private static readonly Regex LegacyTempNamePattern = new(
        @"^\.tmp_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.cbz$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<ArchiveWriteRecoveryService> _logger;
    private readonly TimeProvider _timeProvider;

    public ArchiveWriteRecoveryService(
        IOptionsMonitor<AppSettings> settings,
        ILogger<ArchiveWriteRecoveryService> logger,
        TimeProvider? timeProvider = null)
    {
        _settings = settings;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// A new, unused path in <paramref name="directory"/> for an archive being rewritten.
    /// </summary>
    public static string CreateTempPath(string directory) =>
        Path.Join(directory, TempPrefix + Guid.NewGuid().ToString("N") + TempExtension);

    /// <summary>True for a path made by <see cref="CreateTempPath"/>.</summary>
    public static bool IsTempPath(string path) => TempNamePattern.IsMatch(Path.GetFileName(path));

    /// <summary>
    /// Sweeps the watched and duplicate directories (recursively) and the temp directory, and
    /// logs what it did. Never throws for a file it cannot handle; that file is logged and skipped.
    /// </summary>
    public Task<ArchiveWriteRecoveryResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings.CurrentValue;
        var startedAt = _timeProvider.GetUtcNow().UtcDateTime;
        int tempFilesDeleted = 0, restored = 0, removed = 0, kept = 0;

        foreach (var root in new[] { settings.WatchedDirectory, settings.DuplicateDirectory }
                     .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                     .Distinct(StringComparer.FromComparison(LibraryPathValidator.PathComparison)))
        {
            foreach (var path in EnumerateFiles(root, recurse: true))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                if (TempNamePattern.IsMatch(name))
                {
                    if (TryDeleteStale(path, startedAt))
                    {
                        tempFilesDeleted++;
                    }
                }
                else if (name.EndsWith(BackupExtension, StringComparison.OrdinalIgnoreCase) &&
                         ComicFileExtensions.IsComicArchive(name[..^BackupExtension.Length]))
                {
                    switch (RecoverBackup(path, startedAt))
                    {
                        case BackupOutcome.Restored: restored++; break;
                        case BackupOutcome.Removed: removed++; break;
                        case BackupOutcome.Kept: kept++; break;
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.TempFileDirectory) && Directory.Exists(settings.TempFileDirectory))
        {
            foreach (var path in EnumerateFiles(settings.TempFileDirectory, recurse: false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (LegacyTempNamePattern.IsMatch(Path.GetFileName(path)) && TryDeleteStale(path, startedAt))
                {
                    tempFilesDeleted++;
                }
            }
        }

        var result = new ArchiveWriteRecoveryResult(tempFilesDeleted, restored, removed, kept);
        if (result.DidAnything)
        {
            _logger.LogInformation(
                "Interrupted archive writes recovered: {TempFiles} unfinished temp file(s) deleted, {Restored} archive(s) restored from backup, {Removed} leftover backup(s) removed, {Kept} backup(s) kept",
                tempFilesDeleted, restored, removed, kept);
        }
        else
        {
            _logger.LogDebug("No interrupted archive writes to recover");
        }

        return Task.FromResult(result);
    }

    private enum BackupOutcome { None, Restored, Removed, Kept }

    private BackupOutcome RecoverBackup(string backupPath, DateTime startedAt)
    {
        try
        {
            if (!File.Exists(backupPath) || File.GetLastWriteTimeUtc(backupPath) >= startedAt)
            {
                return BackupOutcome.None;
            }

            var archivePath = backupPath[..^BackupExtension.Length];
            if (File.Exists(archivePath) && IsReadableArchive(archivePath))
            {
                File.Delete(backupPath);
                _logger.LogInformation(
                    "Removed leftover backup {Backup}: the archive it was made for is intact",
                    LoggingHelper.SanitizePathForLog(backupPath));
                return BackupOutcome.Removed;
            }

            if (!IsReadableArchive(backupPath))
            {
                _logger.LogError(
                    "Neither {Archive} nor its backup {Backup} can be read; both are left as they are",
                    LoggingHelper.SanitizePathForLog(archivePath),
                    LoggingHelper.SanitizePathForLog(backupPath));
                return BackupOutcome.Kept;
            }

            // Same directory, so the restore is a single atomic rename over the damaged archive.
            File.Move(backupPath, archivePath, overwrite: true);
            _logger.LogWarning(
                "Restored {Archive} from its backup: an interrupted metadata write had left it missing or unreadable",
                LoggingHelper.SanitizePathForLog(archivePath));
            return BackupOutcome.Restored;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not recover the leftover backup {Backup}", LoggingHelper.SanitizePathForLog(backupPath));
            return BackupOutcome.Kept;
        }
    }

    private bool TryDeleteStale(string path, DateTime startedAt)
    {
        try
        {
            // A temp file written since the sweep started belongs to a write in progress.
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) >= startedAt)
            {
                return false;
            }

            File.Delete(path);
            _logger.LogInformation("Deleted the unfinished archive write {TempFile}", LoggingHelper.SanitizePathForLog(path));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete the unfinished archive write {TempFile}", LoggingHelper.SanitizePathForLog(path));
            return false;
        }
    }

    /// <summary>
    /// True when the archive opens and lists at least one entry. An archive whose in-place
    /// overwrite was interrupted has no central directory and fails to open.
    /// </summary>
    private static bool IsReadableArchive(string path)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(path);
            return archive.Entries.Any(entry => !entry.IsDirectory);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private IEnumerable<string> EnumerateFiles(string directory, bool recurse)
    {
        // Dot files count as hidden on Unix, so only links are skipped: the sweep stays inside
        // the library and never follows a link out of it.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recurse,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        try
        {
            return Directory.EnumerateFiles(directory, "*", options).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not scan {Directory} for interrupted archive writes", LoggingHelper.SanitizePathForLog(directory));
            return Array.Empty<string>();
        }
    }
}
