using System.Collections.Concurrent;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

public sealed partial class FolderCombineService
{
    internal const string CombineJobOperationName = "CombineFoldersJob";
    internal const string HistoryAction = "Combine Folder";
    internal const string NoGroupError = "No combinable folder group matches the request. The library may have changed; please reload.";

    private const int MaxFileRenameAttempts = 1000;

    /// <inheritdoc />
    public async Task<FolderCombineStartResult> StartAsync(FolderCombineRequest? request, CancellationToken cancellationToken = default)
    {
        var planned = await BuildPlanAsync(request, includeSourceCleanup: false, cancellationToken);
        if (planned.Error is not null)
        {
            return FolderCombineStartResult.Failed(planned.Error);
        }

        var plan = planned.Plan!;
        var moves = plan.Moves.Where(m => !m.Skipped).ToList();
        if (moves.Count == 0)
        {
            // Every move was decided as skipped while planning; nothing to run.
            return new FolderCombineStartResult(null, plan, null);
        }

        // Capture the series names of the files about to be moved before the
        // moves re-key them, so they can be remembered as aliases of the
        // destination series (the user implicitly endorsed them by combining).
        var preMoveSeries = await GetPreMoveSeriesNamesAsync(moves.Select(m => m.SourcePath), cancellationToken);
        var run = new CombineRun(this, plan, moves, preMoveSeries);

        var jobId = await _processor.RunCustomBatchJobAsync(
            operationName: CombineJobOperationName,
            trackedItems: run.TrackedItems,
            itemOperation: run.MoveAsync,
            failureMessage: "Combine folder move failed",
            postLoopAsync: run.FinishAsync,
            cancellationToken: cancellationToken);

        return new FolderCombineStartResult(null, plan, jobId);
    }

    /// <summary>
    /// Validates <paramref name="request"/> against freshly built groups and
    /// plans its file moves (numbering a name that is taken in the
    /// destination). With <paramref name="includeSourceCleanup"/> it also
    /// predicts which source folders will be removed.
    /// </summary>
    private async Task<FolderCombinePlanResult> BuildPlanAsync(
        FolderCombineRequest? request,
        bool includeSourceCleanup,
        CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return FolderCombinePlanResult.Failed("Request body is required.");
        }

        if (string.IsNullOrWhiteSpace(request.DestinationDirectory))
        {
            return FolderCombinePlanResult.Failed("Destination directory is required.");
        }

        var destination = Path.GetFullPath(request.DestinationDirectory);
        if (!IsPathSafe(destination))
        {
            _logger.LogWarning("Attempt to combine folders into a destination outside watched directory: {Destination}",
                LoggingHelper.SanitizePathForLog(destination));
            return FolderCombinePlanResult.Failed("Destination directory is outside the allowed directory.");
        }

        var groups = await BuildCombinableFolderGroupsAsync(cancellationToken);
        CombinableFolderGroup? group = null;
        if (!string.IsNullOrWhiteSpace(request.GroupKey))
        {
            group = groups.FirstOrDefault(g => string.Equals(g.GroupKey, request.GroupKey, StringComparison.OrdinalIgnoreCase));

            // Synthetic `series:<id>` group keys are not guaranteed to be in
            // the combinable-folder list (series with a single folder are
            // skipped, the cache may have shifted between requests, etc.).
            // Fall back to resolving via the series library so the per-series
            // Manage Folders UI can always combine.
            if (group is null
                && request.GroupKey!.StartsWith(CombinableFolderGroup.SeriesGroupKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var seriesId = request.GroupKey.Substring(CombinableFolderGroup.SeriesGroupKeyPrefix.Length);
                group = await BuildSeriesScopedCombinableGroupAsync(seriesId, cancellationToken);
            }
        }
        else
        {
            // Infer group from destination + sources by finding a group that contains all directories.
            // Directory comparisons here are case-sensitive (ordinal) so that two
            // on-disk folders differing only by capitalization (possible on
            // case-sensitive filesystems) are treated as distinct paths and the
            // right group is selected.
            var allDirs = new HashSet<string>(StringComparer.Ordinal) { destination };
            foreach (var src in request.SourceDirectories ?? new List<string>())
            {
                allDirs.Add(Path.GetFullPath(src));
            }
            group = groups.FirstOrDefault(g => allDirs.All(d => g.Folders.Any(f => string.Equals(f.Directory, d, StringComparison.Ordinal))));
        }

        if (group == null)
        {
            return FolderCombinePlanResult.Failed(NoGroupError);
        }

        // Determine source folders. If the caller specified them, validate; otherwise use all-but-destination.
        List<CombinableFolder> sourceFolders;
        if (request.SourceDirectories != null && request.SourceDirectories.Count > 0)
        {
            sourceFolders = new List<CombinableFolder>();
            foreach (var src in request.SourceDirectories)
            {
                var srcFull = Path.GetFullPath(src);
                if (!IsPathSafe(srcFull))
                {
                    _logger.LogWarning("Attempt to combine from source outside watched directory: {Source}",
                        LoggingHelper.SanitizePathForLog(srcFull));
                    return FolderCombinePlanResult.Failed("One or more source directories are outside the allowed directory.");
                }
                if (string.Equals(srcFull, destination, StringComparison.Ordinal))
                {
                    continue;
                }
                var folder = group.Folders.FirstOrDefault(f => string.Equals(f.Directory, srcFull, StringComparison.Ordinal));
                if (folder == null)
                {
                    return FolderCombinePlanResult.Failed($"Source directory '{src}' is not part of this group.");
                }
                sourceFolders.Add(folder);
            }
        }
        else
        {
            sourceFolders = group.Folders
                .Where(f => !string.Equals(f.Directory, destination, StringComparison.Ordinal))
                .ToList();
        }

        if (sourceFolders.Count == 0)
        {
            return FolderCombinePlanResult.Failed("No source folders were selected for the combine operation.");
        }

        // Verify destination belongs to the group.
        if (!group.Folders.Any(f => string.Equals(f.Directory, destination, StringComparison.Ordinal)))
        {
            return FolderCombinePlanResult.Failed("Destination directory is not part of this group.");
        }

        var plan = new FolderCombinePlan
        {
            Destination = destination,
            SourceDirectories = sourceFolders.Select(f => f.Directory).ToList(),
            Moves = PlanMoves(destination, sourceFolders)
        };

        if (includeSourceCleanup)
        {
            plan.SourceCleanup = AssessSourceCleanup(plan);
        }

        return FolderCombinePlanResult.Planned(plan);
    }

    /// <summary>
    /// One move per tracked file of the source folders, handling name
    /// collisions in the destination by appending a numeric suffix.
    /// </summary>
    private static List<FolderCombineMove> PlanMoves(string destination, IEnumerable<CombinableFolder> sourceFolders)
    {
        var moves = new List<FolderCombineMove>();
        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(destination))
        {
            foreach (var existing in Directory.EnumerateFiles(destination))
            {
                reservedNames.Add(Path.GetFileName(existing));
            }
        }

        foreach (var folder in sourceFolders)
        {
            foreach (var sourcePath in folder.FilePaths)
            {
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    continue;
                }
                var name = Path.GetFileName(sourcePath);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                var hadConflict = reservedNames.Contains(name);
                var finalName = name;
                if (hadConflict)
                {
                    var baseName = Path.GetFileNameWithoutExtension(name);
                    var extension = Path.GetExtension(name);
                    var attempt = 1;
                    while (reservedNames.Contains(finalName))
                    {
                        finalName = $"{baseName} ({attempt}){extension}";
                        attempt++;
                        if (attempt > MaxFileRenameAttempts)
                        {
                            break;
                        }
                    }
                }
                reservedNames.Add(finalName);
                var destPath = Path.Join(destination, finalName);
                // Use an ordinal (case-sensitive) comparison so we don't
                // skip moves between two folders that differ only by
                // capitalization on case-sensitive filesystems.
                var skipped = string.Equals(sourcePath, destPath, StringComparison.Ordinal);
                moves.Add(new FolderCombineMove
                {
                    SourcePath = sourcePath,
                    DestinationPath = destPath,
                    HadConflict = hadConflict,
                    Skipped = skipped,
                    Reason = skipped ? "Source and destination are the same file." : null
                });
            }
        }

        return moves;
    }

    /// <summary>
    /// Predicts, per source folder, what remains once the planned moves are
    /// done. Folders are assessed deepest first so a nested source folder that
    /// will be removed does not count as a leftover of its parent, matching
    /// the order <see cref="CombineRun.FinishAsync"/> removes them in.
    /// </summary>
    private List<FolderCombineSourceCleanup> AssessSourceCleanup(FolderCombinePlan plan)
    {
        var departing = new HashSet<string>(
            plan.Moves.Where(m => !m.Skipped).Select(m => Path.GetFullPath(m.SourcePath)),
            FolderCombineSourceCleaner.PathComparer);
        var removed = new HashSet<string>(FolderCombineSourceCleaner.PathComparer);
        var protectedDirectories = GetProtectedDirectories(plan.Destination);
        var byDirectory = new Dictionary<string, FolderCombineSourceCleanup>(StringComparer.Ordinal);

        foreach (var directory in DeepestFirst(plan.SourceDirectories))
        {
            var cleanup = FolderCombineSourceCleaner.Assess(directory, departing, removed, protectedDirectories);
            if (cleanup.Removable)
            {
                removed.Add(Path.GetFullPath(directory));
            }
            byDirectory[directory] = cleanup;
        }

        // Report in the plan's source order.
        return plan.SourceDirectories.Select(directory => byDirectory[directory]).ToList();
    }

    private static IEnumerable<string> DeepestFirst(IEnumerable<string> directories) =>
        directories
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(directory => Path.GetFullPath(directory).Length)
            .ThenBy(directory => directory, StringComparer.Ordinal);

    /// <summary>
    /// Returns a map of source-file path -> existing in-memory Series metadata for
    /// the supplied paths, captured *before* a folder-combine move so the values
    /// survive the rename.
    /// </summary>
    private async Task<Dictionary<string, string>> GetPreMoveSeriesNamesAsync(
        IEnumerable<string> sourcePaths,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pathSet = new HashSet<string>(sourcePaths, StringComparer.OrdinalIgnoreCase);
        if (pathSet.Count == 0)
        {
            return result;
        }

        try
        {
            var files = await _fileStore.GetAllFilesAsync(cancellationToken);
            if (files == null)
            {
                return result;
            }

            var requestedFiles = files.Where(file => !string.IsNullOrWhiteSpace(file.FilePath) && pathSet.Contains(file.FilePath));
            foreach (var file in requestedFiles)
            {
                var series = file.Metadata?.Series;
                if (!string.IsNullOrWhiteSpace(series))
                {
                    result[file.FilePath] = series!.Trim();
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Failed to capture pre-move series metadata for folder combine");
        }
        return result;
    }

    /// <summary>
    /// Enumerates the absolute file paths of every tracked file whose parent
    /// directory matches <paramref name="directory"/> (case-insensitive,
    /// trailing-separator tolerant). Used to scoop up the destination folder's
    /// pre-existing files when queuing the post-combine normalize-and-rename.
    /// </summary>
    private async Task<List<string>> GetFilesInDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(directory))
        {
            return result;
        }

        var normalizedDir = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var files = await _fileStore.GetAllFilesAsync(cancellationToken) ?? Enumerable.Empty<ComicFile>();
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.FilePath))
            {
                continue;
            }
            var parent = Path.GetDirectoryName(file.FilePath);
            if (string.IsNullOrEmpty(parent))
            {
                continue;
            }
            var trimmedParent = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(trimmedParent, normalizedDir, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(file.FilePath);
            }
        }
        return result;
    }

    /// <summary>
    /// The state of one running combine job: the per-item move and the
    /// post-loop step (source-folder cleanup, one file-list broadcast, alias
    /// persistence and the normalize-and-rename of the combined folder).
    /// </summary>
    private sealed class CombineRun
    {
        private readonly FolderCombineService _owner;
        private readonly FolderCombinePlan _plan;
        private readonly IReadOnlyDictionary<string, string> _preMoveSeries;
        private readonly Dictionary<string, FolderCombineMove> _movesBySource;

        // Moves of distinct files run in parallel, so the results are
        // collected from worker threads.
        private readonly ConcurrentBag<string> _movedDestinationPaths = new();

        public CombineRun(
            FolderCombineService owner,
            FolderCombinePlan plan,
            IReadOnlyList<FolderCombineMove> moves,
            IReadOnlyDictionary<string, string> preMoveSeries)
        {
            _owner = owner;
            _plan = plan;
            _preMoveSeries = preMoveSeries;
            _movesBySource = moves.ToDictionary(m => m.SourcePath, m => m, StringComparer.OrdinalIgnoreCase);
            TrackedItems = moves.Select(m => m.SourcePath).ToList();
        }

        public IReadOnlyList<string> TrackedItems { get; }

        public async Task<bool> MoveAsync(string sourcePath, CancellationToken token)
        {
            if (!_movesBySource.TryGetValue(sourcePath, out var move))
            {
                return false;
            }

            try
            {
                var destDir = Path.GetDirectoryName(move.DestinationPath);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                // Never overwrite: a file that appeared at the destination
                // since planning fails this move instead.
                File.Move(move.SourcePath, move.DestinationPath);
                // Suppress per-file broadcasts: combining a folder with many files would
                // otherwise emit one file_list_updated SSE event per move, flooding the
                // browser and hanging the site. FinishAsync emits a single broadcast.
                await _owner._fileStore.UpdateFilePathAsync(move.SourcePath, move.DestinationPath, token, broadcastUpdate: false);
                await _owner.LogHistoryAsync(move.SourcePath, HistoryAction, true, $"Moved to {move.DestinationPath}");
                _movedDestinationPaths.Add(move.DestinationPath);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _owner._logger.LogWarning(ex, "Failed to move {Source} to {Destination} during folder combine",
                    LoggingHelper.SanitizePathForLog(move.SourcePath),
                    LoggingHelper.SanitizePathForLog(move.DestinationPath));
                await _owner.LogHistoryAsync(move.SourcePath, HistoryAction, false, ex.Message);
                return false;
            }
        }

        public async Task FinishAsync(IBatchJobReport report, CancellationToken token)
        {
            var removedAny = await RemoveSourceFoldersAsync(report, token);

            // Emit a single file list update broadcast for the entire combine operation
            // so that connected clients refresh once instead of once per file.
            if (!_movedDestinationPaths.IsEmpty || removedAny)
            {
                try
                {
                    await _owner._eventBroadcaster.BroadcastFileListUpdateAsync();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _owner._logger.LogWarning(ex, "Failed to broadcast file list update after folder combine");
                }
            }

            if (_movedDestinationPaths.IsEmpty)
            {
                return;
            }

            // Persist user-canonical / alias info on the destination series
            // and queue a normalize-then-rename batch job so every file in the
            // combined folder reflects the new series name / file format.
            await _owner.PersistFolderCombineAliasesAsync(_plan, _preMoveSeries, token);

            var pathsToProcess = new HashSet<string>(_movedDestinationPaths, StringComparer.OrdinalIgnoreCase);
            try
            {
                var existingDestinationFiles = await _owner.GetFilesInDirectoryAsync(_plan.Destination, token);
                pathsToProcess.UnionWith(existingDestinationFiles.Where(path => !string.IsNullOrWhiteSpace(path)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _owner._logger.LogDebug(ex, "Failed to enumerate existing destination files for {Destination}; only moved files will be re-normalized",
                    LoggingHelper.SanitizePathForLog(_plan.Destination));
            }

            try
            {
                await _owner._processor.NormalizeAndRenameFilesAsync(pathsToProcess, token);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _owner._logger.LogWarning(ex, "Failed to queue normalize-and-rename job after folder combine");
            }
        }

        /// <summary>
        /// Removes each source folder that is left with nothing but disposable
        /// entries, deepest first, holding the library mutation lock for each
        /// so no import publishes into a folder while it is being removed. A
        /// kept folder is reported in the job's results with what kept it,
        /// unless it is one of the library's configured folders, which are
        /// never removed. A folder that cannot be checked is kept and reported
        /// too, and the remaining folders are still handled.
        /// </summary>
        private async Task<bool> RemoveSourceFoldersAsync(IBatchJobReport report, CancellationToken token)
        {
            var protectedDirectories = _owner.GetProtectedDirectories(_plan.Destination);
            var removedAny = false;
            foreach (var sourceDir in DeepestFirst(_plan.SourceDirectories))
            {
                var existed = Directory.Exists(sourceDir);
                FolderCombineSourceCleanup cleanup;
                try
                {
                    cleanup = await _owner._libraryMutationLock.ExecuteAsync(
                        _ => Task.FromResult(FolderCombineSourceCleaner.Remove(sourceDir, protectedDirectories)),
                        token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _owner._logger.LogWarning(ex, "Could not check source folder {Directory} after folder combine",
                        LoggingHelper.SanitizePathForLog(sourceDir));
                    cleanup = FolderCombineSourceCleaner.CouldNotBeChecked(sourceDir, ex);
                }

                if (cleanup.IsLibraryFolder)
                {
                    // Expected, e.g. loose files combined out of the watched
                    // folder: the folder stays and that is not a problem.
                    _owner._logger.LogInformation("Kept source folder {Directory} after folder combine: it is a configured library folder",
                        LoggingHelper.SanitizePathForLog(sourceDir));
                    continue;
                }

                if (cleanup.Removable)
                {
                    if (!existed)
                    {
                        continue;
                    }

                    removedAny = true;
                    var deleted = cleanup.DisposableFileCount == 0
                        ? "it was empty"
                        : $"deleted {cleanup.DisposableFileCount} disposable file(s): {string.Join(", ", cleanup.DisposableFiles)}";
                    _owner._logger.LogInformation("Removed source folder {Directory} after folder combine ({Deleted})",
                        LoggingHelper.SanitizePathForLog(sourceDir), LoggingHelper.SanitizeForLog(deleted));
                    await _owner.LogHistoryAsync(sourceDir, HistoryAction, true, $"Removed the emptied source folder; {deleted}");
                    continue;
                }

                var message = FolderCombineSourceCleaner.DescribeKeptFolder(cleanup);
                _owner._logger.LogInformation("Kept source folder {Directory} after folder combine: {Reason} ({LeftoverCount} leftover item(s))",
                    LoggingHelper.SanitizePathForLog(sourceDir),
                    LoggingHelper.SanitizeForLog(cleanup.KeptReason),
                    cleanup.LeftoverCount);
                await _owner.LogHistoryAsync(sourceDir, HistoryAction, false, message);
                await report.ReportProblemAsync(sourceDir, message);
            }

            return removedAny;
        }
    }
}
