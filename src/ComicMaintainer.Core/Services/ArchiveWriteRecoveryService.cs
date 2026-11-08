using System.Globalization;
using System.Text.RegularExpressions;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.ErrorReporting;
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
/// <param name="BackupsLeftInPlace">
/// Backups next to an intact archive. They are reported but never deleted: this version makes no
/// backups, so such a file may be the user's own copy.
/// </param>
/// <param name="BackupsUnreadable">Backups left in place because neither they nor their archive could be read.</param>
/// <param name="UpgradeCleanupRan">
/// True when the sweep also cleaned up after versions before 3.0, which it does only until that
/// cleanup has completed once.
/// </param>
public sealed record ArchiveWriteRecoveryResult(
    int TempFilesDeleted,
    int BackupsRestored,
    int BackupsLeftInPlace,
    int BackupsUnreadable,
    bool UpgradeCleanupRan)
{
    /// <summary>True when the sweep deleted, restored or reported at least one file.</summary>
    public bool DidAnything => TempFilesDeleted + BackupsRestored + BackupsLeftInPlace + BackupsUnreadable > 0;
}

/// <summary>
/// Cleans up after archive writes that a crash, an OOM kill or a container stop interrupted.
/// </summary>
/// <remarks>
/// <para>A metadata write builds the new archive next to the original under a name that the file
/// watcher, the library scan and this sweep recognise (<see cref="CreateTempPath"/>) and then
/// renames it over the original, so an interrupted write leaves the original intact plus an
/// unfinished temp file. Every startup sweep deletes those temp files. They can sit next to any
/// archive, so the sweep walks the watched and duplicate directories.</para>
/// <para>Versions before 3.0 wrote differently, and the first sweep after an upgrade also cleans
/// up after them. They built the temp archive in <c>TempFileDirectory</c> and, when that was on
/// another mount (the default Docker layout), copied the original to <c>&lt;archive&gt;.backup</c>
/// and overwrote the original in place; their <c>File.Replace</c> could also leave the original
/// renamed to the backup. For every <c>&lt;archive&gt;.backup</c>, the sweep restores the backup when
/// the archive is missing or unreadable and the backup can be read. A backup next to an intact
/// archive is reported and left in place, because it may be the user's own copy. When neither
/// file can be read, both are left alone and an error is logged. The sweep also deletes the old
/// version's orphaned temp archives in <c>TempFileDirectory</c> and the
/// <c>&lt;archive&gt;.cover.tmp</c> files its series cover writer built next to an archive.
/// Once that cleanup has covered the library, a marker file in <c>ConfigDirectory</c>
/// (<see cref="UpgradeMarkerFileName"/>) records it, and later sweeps leave <c>.backup</c> files and
/// those legacy temp files alone. This version never creates any of them, so from then on they
/// are the user's own files.</para>
/// <para>Only files last written before the sweep started are touched, so it can run while new
/// writes are already happening.</para>
/// </remarks>
public sealed class ArchiveWriteRecoveryService
{
    /// <summary>
    /// File in <c>ConfigDirectory</c> recording that the cleanup after versions before 3.0 has
    /// run. Deleting it makes the next startup run that cleanup again.
    /// </summary>
    public const string UpgradeMarkerFileName = "archive-write-recovery-v3.done";

    /// <summary>Prefix the file watcher and the library scan already ignore.</summary>
    private const string TempPrefix = ".tmp_";

    /// <summary>Extension of an archive being rewritten; not a comic extension, so nothing opens it.</summary>
    private const string TempExtension = ".comicinfo-write";

    private const string BackupExtension = ".backup";

    /// <summary>
    /// Suffix of the temp archive the series cover writer of versions before 3.0 built next to
    /// the archive (<c>&lt;archive&gt;.cover.tmp</c>).
    /// </summary>
    private const string LegacyCoverTempSuffix = ".cover.tmp";

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
    /// Sweeps the watched and duplicate directories (recursively), and the temp directory while
    /// the cleanup after an upgrade is due, and logs what it did. Never throws for a file it
    /// cannot handle; that file is logged and skipped.
    /// </summary>
    public Task<ArchiveWriteRecoveryResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings.CurrentValue;
        var startedAt = _timeProvider.GetUtcNow().UtcDateTime;
        var markerPath = GetUpgradeMarkerPath(settings);
        var upgradeCleanup = markerPath is null || !File.Exists(markerPath);
        int tempFilesDeleted = 0, restored = 0, leftInPlace = 0, unreadable = 0;

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
                else if (!upgradeCleanup)
                {
                    continue;
                }
                else if (IsLegacyCoverTempName(name))
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
                        case BackupOutcome.LeftInPlace: leftInPlace++; break;
                        case BackupOutcome.Unreadable: unreadable++; break;
                    }
                }
            }
        }

        if (upgradeCleanup)
        {
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

            RecordUpgradeCleanupDone(settings, markerPath);
        }

        var result = new ArchiveWriteRecoveryResult(tempFilesDeleted, restored, leftInPlace, unreadable, upgradeCleanup);
        if (result.DidAnything)
        {
            _logger.LogInformation(
                "Interrupted archive writes recovered: {TempFiles} unfinished temp file(s) deleted, {Restored} archive(s) restored from backup, {LeftInPlace} backup(s) next to an intact archive left in place, {Unreadable} unreadable backup(s) left in place",
                tempFilesDeleted, restored, leftInPlace, unreadable);
        }
        else
        {
            _logger.LogDebug("No interrupted archive writes to recover");
        }

        return Task.FromResult(result);
    }

    private static string? GetUpgradeMarkerPath(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.ConfigDirectory)
            ? null
            : Path.Join(settings.ConfigDirectory, UpgradeMarkerFileName);

    /// <summary>
    /// Writes the marker that ends the cleanup after an upgrade, once that cleanup has covered the
    /// library. A missing library (an unmounted share, say) leaves it due for the next startup.
    /// </summary>
    private void RecordUpgradeCleanupDone(AppSettings settings, string? markerPath)
    {
        if (markerPath is null ||
            string.IsNullOrWhiteSpace(settings.WatchedDirectory) || !Directory.Exists(settings.WatchedDirectory) ||
            !Directory.Exists(settings.ConfigDirectory))
        {
            _logger.LogDebug("The cleanup after archive writes of versions before 3.0 runs again at the next startup");
            return;
        }

        try
        {
            File.WriteAllText(
                markerPath,
                "Leftover backup and temp files of archive writes by versions before 3.0 were cleaned up on " +
                _timeProvider.GetUtcNow().ToString("u", CultureInfo.InvariantCulture) +
                ". Delete this file to run that cleanup again at the next startup." + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Could not record that the cleanup after archive writes of versions before 3.0 has run; it runs again at the next startup");
        }
    }

    private static bool IsLegacyCoverTempName(string name) =>
        name.EndsWith(LegacyCoverTempSuffix, StringComparison.OrdinalIgnoreCase) &&
        ComicFileExtensions.IsComicArchive(name[..^LegacyCoverTempSuffix.Length]);

    private enum BackupOutcome { None, Restored, LeftInPlace, Unreadable }

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
                _logger.LogWarning(
                    "Found {Backup} next to the intact archive it was made for. Versions before 3.0 could leave such a backup after an interrupted metadata write; it is left in place, so delete it if you do not need it",
                    LoggingHelper.SanitizePathForLog(backupPath));
                return BackupOutcome.LeftInPlace;
            }

            if (!IsReadableArchive(backupPath))
            {
                // A damaged file in the library is not a defect of the application.
                using (_logger.BeginScope(ErrorReportLogProperties.Exclude("damaged archive and backup in the library")))
                {
                    _logger.LogError(
                        "Neither {Archive} nor its backup {Backup} can be read; both are left as they are",
                        LoggingHelper.SanitizePathForLog(archivePath),
                        LoggingHelper.SanitizePathForLog(backupPath));
                }

                return BackupOutcome.Unreadable;
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
            return BackupOutcome.Unreadable;
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
