using System.IO.Compression;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpCompress.Archives.Rar;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Moves chapters Suwayomi has finished downloading out of its downloads
/// folder, where they sit as <c>&lt;source&gt;/&lt;series&gt;/&lt;chapter&gt;.cbz</c>,
/// into the library as <c>&lt;series folder&gt;/&lt;chapter&gt;.cbz</c>. The
/// file watcher then normalizes and renames them like any other new file.
/// </summary>
/// <remarks>
/// The series folder a chapter goes to is, in order of preference: the folder
/// already chosen for the same series earlier in the pass; a folder in the
/// import directory named like the Suwayomi series folder (ignoring case,
/// spacing and punctuation); the folder holding most of that series' issues
/// elsewhere in the library; or a new folder in the import directory named
/// after the Suwayomi series folder.
/// </remarks>
public sealed class SuwayomiDownloadImportService : ISuwayomiDownloadImportService
{
    // Suwayomi streams a CBZ straight into its final path, so an archive that
    // changed recently may still be growing. Never import one younger than
    // this, or than the watcher's own stability delay when that is longer.
    private static readonly TimeSpan MinimumSettleTime = TimeSpan.FromSeconds(30);

    // Prefix the file watcher ignores; an archive carries it while moving in.
    private const string StagingPrefix = ".tmp_";

    private const int MaxNameAttempts = 1000;

    // A chapter saved as loose pages holds many images; a lone image (such as
    // a cover) does not make a chapter folder.
    private const int MinImagesForChapterFolder = 2;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".jxl", ".bmp"
    };

    // Never follow links out of the downloads folder, and skip hidden entries
    // such as staging files. A folder that cannot be listed throws, so the
    // run reports the permission problem instead of silently missing chapters.
    private static readonly EnumerationOptions ScanOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System
    };

    private static readonly EnumerationOptions ChildFolderOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
    };

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ISeriesLibraryService _seriesLibrary;
    private readonly ILogger<SuwayomiDownloadImportService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SuwayomiDownloadImportService(
        IOptionsMonitor<AppSettings> settings,
        ISeriesLibraryService seriesLibrary,
        ILogger<SuwayomiDownloadImportService> logger,
        TimeProvider? timeProvider = null)
    {
        _settings = settings;
        _seriesLibrary = seriesLibrary;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SuwayomiImportResult> ImportAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings.CurrentValue;
        if (string.IsNullOrWhiteSpace(settings.SuwayomiDownloadsDirectory))
        {
            return SuwayomiImportResult.Skipped("No Suwayomi downloads folder is configured.", notConfigured: true);
        }

        // Re-validated on every pass: the folders may come from environment
        // variables, and the library directories can change after a save.
        if (!TryResolveDownloadsDirectory(settings.SuwayomiDownloadsDirectory, settings, out var downloadsRoot, out var error)
            || !TryResolveImportDirectory(settings.SuwayomiImportDirectory, settings, out var importRoot, out error)
            || !TryResolvePath(settings.WatchedDirectory, out var watchedRoot))
        {
            error ??= "The watched directory is not a valid path.";
            _logger.LogWarning("Suwayomi download import skipped: {Reason}", LoggingHelper.SanitizeForLog(error));
            return SuwayomiImportResult.Skipped(error);
        }

        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return SuwayomiImportResult.Skipped("Another Suwayomi download import is already running.");
        }

        try
        {
            List<string> archives;
            int imageFolders;
            try
            {
                (archives, imageFolders) = ScanDownloads(downloadsRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not read the Suwayomi downloads folder {Folder}", LoggingHelper.SanitizeForLog(downloadsRoot));
                return SuwayomiImportResult.Skipped($"The Suwayomi downloads folder could not be read: {ex.Message}");
            }

            var run = new ImportRun(settings, downloadsRoot, importRoot, watchedRoot, _timeProvider.GetUtcNow().UtcDateTime);
            run.Result.ImageFolders = imageFolders;

            await ImportArchivesAsync(run, archives, cancellationToken);
            run.Result.RemovedFolders = RemoveEmptyFolders(run);
            return run.Result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Validates the Suwayomi downloads folder: an absolute path to an
    /// existing folder that neither contains nor sits inside the watched,
    /// duplicate, config or temp directories.
    /// </summary>
    /// <param name="resolvedPath">The physical folder path, links resolved.</param>
    /// <param name="error">Why the folder was refused; null on success.</param>
    public static bool TryResolveDownloadsDirectory(
        string? configured,
        AppSettings settings,
        out string resolvedPath,
        out string? error)
    {
        resolvedPath = string.Empty;
        error = null;

        var trimmed = configured?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            error = "No Suwayomi downloads folder is configured.";
            return false;
        }

        if (!Path.IsPathFullyQualified(trimmed))
        {
            error = $"The Suwayomi downloads folder '{trimmed}' must be an absolute path.";
            return false;
        }

        if (!TryResolvePath(trimmed, out var resolved))
        {
            error = $"The Suwayomi downloads folder '{trimmed}' is not a valid path.";
            return false;
        }

        if (IsVolumeRoot(resolved))
        {
            error = "The Suwayomi downloads folder cannot be the root of a drive.";
            return false;
        }

        foreach (var (name, directory) in ProtectedDirectories(settings))
        {
            if (TryResolvePath(directory, out var protectedPath)
                && (IsInside(resolved, protectedPath) || IsInside(protectedPath, resolved)))
            {
                error = $"The Suwayomi downloads folder must be kept apart from the {name} ('{directory}'): neither may contain the other.";
                return false;
            }
        }

        if (!Directory.Exists(resolved))
        {
            error = $"The Suwayomi downloads folder '{trimmed}' does not exist or is not accessible.";
            return false;
        }

        resolvedPath = resolved;
        return true;
    }

    /// <summary>
    /// Validates the library folder imported chapters go into. Empty means the
    /// watched directory; a relative path is resolved against it and an
    /// absolute one must lie inside it. The folder need not exist yet.
    /// </summary>
    /// <param name="resolvedPath">The physical folder path, links resolved.</param>
    /// <param name="error">Why the folder was refused; null on success.</param>
    public static bool TryResolveImportDirectory(
        string? configured,
        AppSettings settings,
        out string resolvedPath,
        out string? error)
    {
        resolvedPath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(settings.WatchedDirectory))
        {
            error = "No watched directory is configured.";
            return false;
        }

        var trimmed = configured?.Trim();
        var candidate = string.IsNullOrEmpty(trimmed)
            ? settings.WatchedDirectory
            : Path.Combine(settings.WatchedDirectory, trimmed);

        if (!LibraryPathValidator.TryResolveWithin(candidate, out var resolved, settings.WatchedDirectory))
        {
            error = $"The import folder '{trimmed}' must be inside the watched directory ('{settings.WatchedDirectory}').";
            return false;
        }

        foreach (var (name, directory) in ProtectedDirectories(settings).Skip(1))
        {
            if (TryResolvePath(directory, out var protectedPath) && IsInside(resolved, protectedPath))
            {
                error = $"The import folder must not be inside the {name} ('{directory}').";
                return false;
            }
        }

        resolvedPath = Path.TrimEndingDirectorySeparator(resolved);
        return true;
    }

    /// <summary>
    /// True when <paramref name="path"/> opens as an archive with at least one
    /// entry. Suwayomi writes a CBZ's central directory last, so an archive it
    /// is still writing does not open.
    /// </summary>
    public static bool IsCompleteArchive(string path)
        => ProbeArchive(path) == ArchiveState.Complete;

    private static ArchiveState ProbeArchive(string path)
    {
        try
        {
            if (ComicFileExtensions.NormalizeExtension(Path.GetExtension(path)) == ".cbz")
            {
                using var zip = ZipFile.OpenRead(path);
                return zip.Entries.Count > 0 ? ArchiveState.Complete : ArchiveState.Incomplete;
            }

            using var rar = RarArchive.OpenArchive(path);
            return rar.Entries.Any() ? ArchiveState.Complete : ArchiveState.Incomplete;
        }
        catch (UnauthorizedAccessException)
        {
            return ArchiveState.PermissionDenied;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ArchiveState.Incomplete;
        }
    }

    private async Task ImportArchivesAsync(ImportRun run, List<string> archives, CancellationToken cancellationToken)
    {
        var result = run.Result;
        foreach (var archive in archives)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var seriesFolder = Path.GetDirectoryName(archive);
            if (seriesFolder is null || PathsEqual(seriesFolder, run.DownloadsRoot))
            {
                result.WithoutSeriesFolder++;
                continue;
            }

            var file = new FileInfo(archive);
            if (!file.Exists)
            {
                continue;
            }

            if (run.Now - file.LastWriteTimeUtc < run.SettleTime)
            {
                result.StillDownloading++;
                continue;
            }

            var state = ProbeArchive(archive);
            if (state == ArchiveState.PermissionDenied)
            {
                // Permissions rarely differ per file, so stop like a refused move.
                _logger.LogWarning("Permission denied reading Suwayomi download {File}", LoggingHelper.SanitizePathForLog(archive));
                result.Failed++;
                result.PermissionDenied = true;
                return;
            }

            if (state != ArchiveState.Complete)
            {
                result.Unreadable++;
                _logger.LogDebug("Leaving unreadable Suwayomi download {File} in place", LoggingHelper.SanitizePathForLog(archive));
                continue;
            }

            var target = await ResolveTargetFolderAsync(run, Path.GetFileName(seriesFolder), cancellationToken);
            if (target is null)
            {
                result.Failed++;
                continue;
            }

            switch (MoveIntoFolder(archive, target, out var destination))
            {
                case MoveOutcome.Moved:
                    result.Imported.Add(new SuwayomiImportedChapter(archive, destination));
                    MarkEmptied(run, seriesFolder);
                    _logger.LogInformation(
                        "Imported Suwayomi chapter {File} into {Folder}",
                        LoggingHelper.SanitizePathForLog(destination),
                        LoggingHelper.SanitizeForLog(target));
                    break;

                case MoveOutcome.Failed:
                    result.Failed++;
                    break;

                case MoveOutcome.PermissionDenied:
                    // Permissions rarely differ per file, and a refused move
                    // across file systems still costs a full copy, so stop.
                    result.Failed++;
                    result.PermissionDenied = true;
                    return;
            }
        }
    }

    private async Task<string?> ResolveTargetFolderAsync(ImportRun run, string seriesFolderName, CancellationToken cancellationToken)
    {
        var key = SuwayomiService.Normalize(seriesFolderName);
        var cacheKey = key.Length > 0 ? key : seriesFolderName;
        if (run.Targets.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var target = FindImportFolder(run, seriesFolderName, key);
        if (target is null && key.Length > 0)
        {
            run.Library ??= await LoadLibraryIndexAsync(cancellationToken);
            target = FindLibraryFolder(run, key);
        }

        target ??= Path.Combine(run.ImportRoot, seriesFolderName);

        string? confined = null;
        if (TryConfineToLibrary(target, run.Settings, out var resolved))
        {
            confined = resolved;
        }
        else
        {
            _logger.LogWarning(
                "Not importing Suwayomi series {Series}: its folder {Folder} is outside the library",
                LoggingHelper.SanitizeForLog(seriesFolderName),
                LoggingHelper.SanitizeForLog(target));
        }

        run.Targets[cacheKey] = confined;
        return confined;
    }

    private static string? FindImportFolder(ImportRun run, string seriesFolderName, string key)
    {
        var exact = Path.Combine(run.ImportRoot, seriesFolderName);
        if (Directory.Exists(exact))
        {
            return exact;
        }

        if (key.Length == 0)
        {
            return null;
        }

        run.ImportFolders ??= LoadChildFolders(run.ImportRoot);
        return run.ImportFolders.TryGetValue(key, out var match) ? match : null;
    }

    private static Dictionary<string, string> LoadChildFolders(string root)
    {
        var folders = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(root, "*", ChildFolderOptions).Order(StringComparer.Ordinal))
            {
                var key = SuwayomiService.Normalize(Path.GetFileName(folder));
                if (key.Length > 0)
                {
                    folders.TryAdd(key, folder);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Missing (it is created on first import) or unreadable: no matches.
        }

        return folders;
    }

    private static string? FindLibraryFolder(ImportRun run, string key)
    {
        var folders = run.Library?.FindFolders(key);
        if (folders is null)
        {
            return null;
        }

        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder.Directory)
                || !Directory.Exists(folder.Directory)
                || !TryConfineToLibrary(folder.Directory, run.Settings, out var resolved))
            {
                continue;
            }

            // Issues kept loose in the library root have no series folder to join.
            if (PathsEqual(resolved, run.WatchedRoot) || PathsEqual(resolved, run.ImportRoot))
            {
                continue;
            }

            return resolved;
        }

        return null;
    }

    private async Task<LibraryIndex> LoadLibraryIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            var summaries = await _seriesLibrary.GetSeriesSummariesAsync(
                page: 1,
                perPage: -1,
                cancellationToken: cancellationToken);

            var seriesIdsByKey = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var series in summaries.Series)
            {
                foreach (var title in series.Aliases.Prepend(series.CanonicalTitle).Prepend(series.Title))
                {
                    var key = string.IsNullOrWhiteSpace(title) ? string.Empty : SuwayomiService.Normalize(title);
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    if (!seriesIdsByKey.TryGetValue(key, out var ids))
                    {
                        seriesIdsByKey[key] = ids = new HashSet<string>(StringComparer.Ordinal);
                    }

                    ids.Add(series.Id);
                }
            }

            if (seriesIdsByKey.Count == 0)
            {
                return LibraryIndex.Empty;
            }

            var foldersById = new Dictionary<string, List<SeriesFolderDto>>(StringComparer.Ordinal);
            foreach (var group in await _seriesLibrary.GetAllSeriesFolderGroupsAsync(cancellationToken: cancellationToken))
            {
                foldersById.TryAdd(group.Id, group.Folders);
            }

            return new LibraryIndex(seriesIdsByKey, foldersById);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not read the library's series while importing Suwayomi chapters; new series folders will be created instead");
            return LibraryIndex.Empty;
        }
    }

    private MoveOutcome MoveIntoFolder(string sourcePath, string targetFolder, out string destinationPath)
    {
        destinationPath = string.Empty;
        var fileName = Path.GetFileName(sourcePath);
        var extension = Path.GetExtension(fileName);

        try
        {
            Directory.CreateDirectory(targetFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Could not create library folder {Folder} for Suwayomi chapter {File}",
                LoggingHelper.SanitizeForLog(targetFolder),
                LoggingHelper.SanitizePathForLog(sourcePath));
            return ex is UnauthorizedAccessException ? MoveOutcome.PermissionDenied : MoveOutcome.Failed;
        }

        // Stage under a name the file watcher ignores so a copy across file
        // systems is never processed half-written; the rename to the real
        // name below stays within one folder and is atomic.
        var stagingPath = Path.Combine(targetFolder, StagingPrefix + Guid.NewGuid().ToString("N") + extension);
        try
        {
            File.Move(sourcePath, stagingPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (File.Exists(sourcePath))
            {
                // A move across file systems copies, then deletes the source.
                // If the delete was refused, drop the copy so the chapter does
                // not end up both in the library and in Suwayomi's folder.
                TryDeleteFile(stagingPath);
                _logger.LogWarning(
                    ex,
                    "Could not move Suwayomi chapter {File} into {Folder}; ComicMaintainer needs write access to both folders",
                    LoggingHelper.SanitizePathForLog(sourcePath),
                    LoggingHelper.SanitizeForLog(targetFolder));
                return ex is UnauthorizedAccessException ? MoveOutcome.PermissionDenied : MoveOutcome.Failed;
            }

            if (!File.Exists(stagingPath) || !IsCompleteArchive(stagingPath))
            {
                // Deleted from Suwayomi's folder while it was being moved.
                TryDeleteFile(stagingPath);
                return MoveOutcome.Vanished;
            }
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        for (var attempt = 1; attempt <= MaxNameAttempts; attempt++)
        {
            var candidate = Path.Combine(targetFolder, attempt == 1 ? fileName : $"{baseName} ({attempt}){extension}");
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                continue;
            }

            try
            {
                File.Move(stagingPath, candidate, overwrite: false);
                destinationPath = candidate;
                return MoveOutcome.Moved;
            }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate))
            {
                // Another file claimed the name first; try the next one.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    ex,
                    "Could not name imported Suwayomi chapter {File} in {Folder}",
                    LoggingHelper.SanitizePathForLog(sourcePath),
                    LoggingHelper.SanitizeForLog(targetFolder));
                break;
            }
        }

        RestoreStagedFile(stagingPath, sourcePath);
        return MoveOutcome.Failed;
    }

    private void RestoreStagedFile(string stagingPath, string sourcePath)
    {
        try
        {
            File.Move(stagingPath, sourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Could not return Suwayomi chapter {File} to the downloads folder; it was left at {StagingPath}",
                LoggingHelper.SanitizePathForLog(sourcePath),
                LoggingHelper.SanitizeForLog(stagingPath));
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: nothing more can be done about a stray staging file.
        }
    }

    private static void MarkEmptied(ImportRun run, string seriesFolder)
    {
        for (var folder = seriesFolder;
             folder is not null && !PathsEqual(folder, run.DownloadsRoot);
             folder = Path.GetDirectoryName(folder))
        {
            run.EmptiedFolders.Add(folder);
        }
    }

    /// <summary>
    /// Removes empty folders below the downloads root (never the root itself),
    /// deepest first so emptied parents go in the same pass. A folder this pass
    /// did not empty is kept while it is newer than the settle time, since it
    /// may be one Suwayomi has just created for a download.
    /// </summary>
    private int RemoveEmptyFolders(ImportRun run)
    {
        List<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(run.DownloadsRoot, "*", ScanOptions).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var removed = 0;
        foreach (var folder in folders.OrderByDescending(folder => folder.Length))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    continue;
                }

                if (!run.EmptiedFolders.Contains(folder)
                    && run.Now - Directory.GetLastWriteTimeUtc(folder) < run.SettleTime)
                {
                    continue;
                }

                Directory.Delete(folder, recursive: false);
                removed++;

                // Deleting a folder touches its parent, which would otherwise
                // look freshly created; it was emptied by this pass too.
                if (Path.GetDirectoryName(folder) is { } parent && !PathsEqual(parent, run.DownloadsRoot))
                {
                    run.EmptiedFolders.Add(parent);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Refilled meanwhile, or not ours to delete: leave it.
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation("Removed {Count} empty folder(s) from the Suwayomi downloads folder", removed);
        }

        return removed;
    }

    private static (List<string> Archives, int ImageFolders) ScanDownloads(string root)
    {
        var archives = new List<string>();
        var imagesPerFolder = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(root, "*", ScanOptions))
        {
            if (Path.GetFileName(path).StartsWith(StagingPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ComicFileExtensions.IsComicArchive(path))
            {
                archives.Add(path);
            }
            else if (ImageExtensions.Contains(Path.GetExtension(path)) && Path.GetDirectoryName(path) is { } folder)
            {
                imagesPerFolder[folder] = imagesPerFolder.GetValueOrDefault(folder) + 1;
            }
        }

        // Loose chapter pages sit at least two levels down (series/chapter);
        // shallower image folders are e.g. Suwayomi's own thumbnails.
        var imageFolders = imagesPerFolder.Count(entry =>
            entry.Value >= MinImagesForChapterFolder && Depth(root, entry.Key) >= 2);

        archives.Sort(StringComparer.Ordinal);
        return (archives, imageFolders);
    }

    private static int Depth(string root, string folder)
        => Path.GetRelativePath(root, folder)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Length;

    private static IEnumerable<(string Name, string Directory)> ProtectedDirectories(AppSettings settings)
    {
        // The watched directory must stay first: the import folder lives
        // inside it and skips that entry.
        yield return ("watched directory", settings.WatchedDirectory);
        yield return ("duplicates directory", settings.DuplicateDirectory);
        yield return ("config directory", settings.ConfigDirectory);
        yield return ("temp directory", settings.TempFileDirectory);
    }

    private static bool TryConfineToLibrary(string path, AppSettings settings, out string resolvedPath)
    {
        if (!LibraryPathValidator.TryResolveWithin(path, out resolvedPath, settings.WatchedDirectory))
        {
            return false;
        }

        foreach (var (_, directory) in ProtectedDirectories(settings).Skip(1))
        {
            if (TryResolvePath(directory, out var protectedPath) && IsInside(resolvedPath, protectedPath))
            {
                resolvedPath = string.Empty;
                return false;
            }
        }

        return true;
    }

    private static bool TryResolvePath(string? path, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            resolvedPath = LibraryPathValidator.ResolveRealPath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool IsInside(string resolvedPath, string resolvedDirectory)
    {
        if (IsVolumeRoot(resolvedDirectory))
        {
            return true;
        }

        var directory = Path.TrimEndingDirectorySeparator(resolvedDirectory);
        var path = Path.TrimEndingDirectorySeparator(resolvedPath);
        return string.Equals(path, directory, PathComparison)
            || path.StartsWith(directory + Path.DirectorySeparatorChar, PathComparison);
    }

    private static bool IsVolumeRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && PathsEqual(path, root);
    }

    private static bool PathsEqual(string first, string second)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(first),
            Path.TrimEndingDirectorySeparator(second),
            PathComparison);

    private enum MoveOutcome
    {
        Moved,
        Vanished,
        Failed,
        PermissionDenied
    }

    private enum ArchiveState
    {
        Complete,
        Incomplete,
        PermissionDenied
    }

    private sealed class ImportRun
    {
        public ImportRun(AppSettings settings, string downloadsRoot, string importRoot, string watchedRoot, DateTime now)
        {
            Settings = settings;
            DownloadsRoot = downloadsRoot;
            ImportRoot = importRoot;
            WatchedRoot = watchedRoot;
            Now = now;
            SettleTime = TimeSpan.FromSeconds(Math.Max(MinimumSettleTime.TotalSeconds, settings.WatcherFileStabilityDelaySeconds));
        }

        public AppSettings Settings { get; }
        public string DownloadsRoot { get; }
        public string ImportRoot { get; }
        public string WatchedRoot { get; }
        public DateTime Now { get; }
        public TimeSpan SettleTime { get; }
        public SuwayomiImportResult Result { get; } = new();

        /// <summary>Chosen library folder per normalized series name (null: refused).</summary>
        public Dictionary<string, string?> Targets { get; } = new(StringComparer.Ordinal);

        /// <summary>Import-folder children by normalized name; loaded on first use.</summary>
        public Dictionary<string, string>? ImportFolders { get; set; }

        /// <summary>Library series by normalized title; loaded on first use.</summary>
        public LibraryIndex? Library { get; set; }

        /// <summary>Downloads sub-folders this pass moved chapters out of.</summary>
        public HashSet<string> EmptiedFolders { get; } = new(StringComparer.Ordinal);
    }

    private sealed class LibraryIndex
    {
        public static readonly LibraryIndex Empty = new(
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal),
            new Dictionary<string, List<SeriesFolderDto>>(StringComparer.Ordinal));

        private readonly Dictionary<string, HashSet<string>> _seriesIdsByKey;
        private readonly Dictionary<string, List<SeriesFolderDto>> _foldersById;

        public LibraryIndex(
            Dictionary<string, HashSet<string>> seriesIdsByKey,
            Dictionary<string, List<SeriesFolderDto>> foldersById)
        {
            _seriesIdsByKey = seriesIdsByKey;
            _foldersById = foldersById;
        }

        /// <summary>
        /// The folders of the one library series known by <paramref name="key"/>,
        /// largest first; null when no series, or more than one, has that name.
        /// </summary>
        public IReadOnlyList<SeriesFolderDto>? FindFolders(string key)
            => _seriesIdsByKey.TryGetValue(key, out var ids)
               && ids.Count == 1
               && _foldersById.TryGetValue(ids.First(), out var folders)
                ? folders
                : null;
    }
}
