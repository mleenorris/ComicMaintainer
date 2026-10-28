using ComicMaintainer.Core.Utilities;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

public partial class FilesController
{
    [HttpPost("combine-folders/preview")]
    public async Task<ActionResult<object>> PreviewCombineFolders(
        [FromBody] CombineFoldersRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (error, plan) = await BuildCombineFoldersPlanAsync(request, cancellationToken);
            if (error != null)
            {
                return BadRequest(new { error });
            }

            return Ok(new
            {
                destination = plan!.Destination,
                sources = plan.SourceDirectories,
                moves = plan.Moves.Select(m => new
                {
                    sourcePath = m.SourcePath,
                    destinationPath = m.DestinationPath,
                    conflict = m.HadConflict,
                    skipped = m.Skipped,
                    reason = m.Reason
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building combine-folders preview");
            return StatusCode(500, "Error building combine preview");
        }
    }

    [HttpPost("combine-folders")]
    public async Task<ActionResult<object>> CombineFolders(
        [FromBody] CombineFoldersRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (error, plan) = await BuildCombineFoldersPlanAsync(request, cancellationToken);
            if (error != null)
            {
                return BadRequest(new { error });
            }

            // Skipped entries are decided synchronously when the plan was built;
            // they need no work and can be reported in the immediate response.
            var skippedResults = plan!.Moves
                .Where(m => m.Skipped)
                .Select(m => new { sourcePath = m.SourcePath, destinationPath = m.DestinationPath, status = "skipped", reason = m.Reason })
                .ToList();

            var nonSkippedMoves = plan.Moves.Where(m => !m.Skipped).ToList();

            // Nothing to move — return immediately with the skipped results so
            // the UI doesn't have to track an empty job.
            if (nonSkippedMoves.Count == 0)
            {
                return Accepted(new
                {
                    destination = plan.Destination,
                    sources = plan.SourceDirectories,
                    moved = 0,
                    skipped = skippedResults.Count,
                    failed = 0,
                    totalItems = 0,
                    results = skippedResults
                });
            }

            // Capture in-memory series metadata for the files about to be moved so
            // we can persist their existing series names (which the user has
            // implicitly endorsed by combining the folders) as aliases on the
            // destination series. This must happen *before* the moves because the
            // in-memory ComicFile list keys off the original source paths.
            var preMoveSeriesBySourcePath = await GetPreMoveSeriesNamesAsync(
                nonSkippedMoves.Select(m => m.SourcePath),
                cancellationToken);

            // Index moves by source path so the per-item job operation can look
            // up the destination without rebuilding the plan.
            var movesBySource = nonSkippedMoves.ToDictionary(
                m => m.SourcePath,
                m => m,
                StringComparer.OrdinalIgnoreCase);

            // Concurrent collection — File.Move on distinct files is safe in
            // parallel, but the result list is mutated from worker threads.
            var movedDestinationPaths = new System.Collections.Concurrent.ConcurrentBag<string>();

            var trackedItems = nonSkippedMoves.Select(m => m.SourcePath).ToList();

            var jobId = await _processor.RunCustomBatchJobAsync(
                operationName: "CombineFoldersJob",
                trackedItems: trackedItems,
                itemOperation: async (sourcePath, token) =>
                {
                    if (!movesBySource.TryGetValue(sourcePath, out var move))
                    {
                        return false;
                    }
                    try
                    {
                        var destDir = Path.GetDirectoryName(move.DestinationPath);
                        if (!string.IsNullOrEmpty(destDir))
                        {
                            System.IO.Directory.CreateDirectory(destDir);
                        }

                        System.IO.File.Move(move.SourcePath, move.DestinationPath);
                        // Suppress per-file broadcasts: combining a folder with many files would
                        // otherwise emit one file_list_updated SSE event per move, flooding the
                        // browser and hanging the site. We emit a single broadcast after the loop.
                        await _fileStore.UpdateFilePathAsync(move.SourcePath, move.DestinationPath, token, broadcastUpdate: false);
                        await LogHistoryAsync(move.SourcePath, "Combine Folder", true,
                            $"Moved to {move.DestinationPath}");
                        movedDestinationPaths.Add(move.DestinationPath);
                        return true;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(ex, "Failed to move {Source} to {Destination} during folder combine",
                            LoggingHelper.SanitizePathForLog(move.SourcePath),
                            LoggingHelper.SanitizePathForLog(move.DestinationPath));
                        await LogHistoryAsync(move.SourcePath, "Combine Folder", false, ex.Message);
                        return false;
                    }
                },
                failureMessage: "Combine folder move failed",
                postLoopAsync: async (token) =>
                {
                    // Remove source directories the files were moved out of. A
                    // directory is removed when it no longer contains any comic
                    // archives (even if leftover non-comic files such as cover
                    // images or ComicInfo.xml remain). If a comic failed to move
                    // it stays behind, so we keep the folder for that case.
                    var removedDirectories = new List<string>();
                    foreach (var sourceDir in plan.SourceDirectories)
                    {
                        try
                        {
                            SuwayomiImportSynchronization.Execute(() =>
                            {
                                if (System.IO.Directory.Exists(sourceDir) &&
                                    !DirectoryContainsComicArchives(sourceDir) &&
                                    !DirectoryContainsSuwayomiStagingFiles(sourceDir))
                                {
                                    System.IO.Directory.Delete(sourceDir, recursive: true);
                                    removedDirectories.Add(sourceDir);
                                }
                            });
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            _logger.LogDebug(ex, "Could not remove source directory {Directory} after folder combine",
                                LoggingHelper.SanitizePathForLog(sourceDir));
                        }
                    }

                    // Emit a single file list update broadcast for the entire combine operation
                    // so that connected clients refresh once instead of once per file.
                    if ((!movedDestinationPaths.IsEmpty || removedDirectories.Count > 0) && _eventBroadcaster != null)
                    {
                        try
                        {
                            await _eventBroadcaster.BroadcastFileListUpdateAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to broadcast file list update after folder combine");
                        }
                    }

                    // Persist user-canonical / alias info on the destination
                    // series and queue a normalize-then-rename batch job so
                    // every file in the combined folder reflects the new
                    // series name / file format.
                    if (!movedDestinationPaths.IsEmpty)
                    {
                        try
                        {
                            await PersistFolderCombineAliasesAsync(plan, preMoveSeriesBySourcePath, token);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to persist folder-combine aliases for {Destination}",
                                LoggingHelper.SanitizePathForLog(plan.Destination));
                        }

                        var pathsToProcess = new HashSet<string>(movedDestinationPaths, StringComparer.OrdinalIgnoreCase);
                        try
                        {
                            var existingDestinationFiles = await GetFilesInDirectoryAsync(plan.Destination, token);
                            foreach (var existingPath in existingDestinationFiles)
                            {
                                if (!string.IsNullOrWhiteSpace(existingPath))
                                {
                                    pathsToProcess.Add(existingPath);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to enumerate existing destination files for {Destination}; only moved files will be re-normalized",
                                LoggingHelper.SanitizePathForLog(plan.Destination));
                        }

                        try
                        {
                            await _processor.NormalizeAndRenameFilesAsync(pathsToProcess, token);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to queue normalize-and-rename job after folder combine");
                        }
                    }
                },
                cancellationToken: cancellationToken);

            // Return immediately with the job id and the plan summary so the UI
            // can attach its progress modal via the standard SSE pipeline. The
            // actual moved/failed counts come from job_updated events.
            return Accepted(new
            {
                jobId = jobId.ToString(),
                job_id = jobId.ToString(),
                totalItems = trackedItems.Count,
                total_items = trackedItems.Count,
                destination = plan.Destination,
                sources = plan.SourceDirectories,
                skipped = skippedResults.Count,
                skippedResults
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error combining folders");
            return StatusCode(500, "Error combining folders");
        }
    }

    private const int MaxFileRenameAttempts = 1000;

    private async Task<(string? Error, CombineFoldersPlan? Plan)> BuildCombineFoldersPlanAsync(
        CombineFoldersRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return ("Request body is required.", null);
        }

        if (string.IsNullOrWhiteSpace(request.DestinationDirectory))
        {
            return ("Destination directory is required.", null);
        }

        var destination = Path.GetFullPath(request.DestinationDirectory);
        if (!IsPathSafe(destination))
        {
            _logger.LogWarning("Attempt to combine folders into a destination outside watched directory: {Destination}",
                LoggingHelper.SanitizePathForLog(destination));
            return ("Destination directory is outside the allowed directory.", null);
        }

        var groups = await BuildCombinableFolderGroupsAsync(cancellationToken);
        CombinableFolderGroup? group = null;
        if (!string.IsNullOrWhiteSpace(request.GroupKey))
        {
            group = groups.FirstOrDefault(g => string.Equals(g.GroupKey, request.GroupKey, StringComparison.OrdinalIgnoreCase));

            // Synthetic `series:<id>` group keys are not guaranteed to be in
            // the combinable-folder list (BuildCombinableFolderGroupsAsync
            // skips series with a single folder, the cache may have shifted
            // between requests, etc.). Fall back to resolving via the series
            // library so the per-series Manage Folders UI can always combine.
            if (group is null
                && request.GroupKey!.StartsWith(SeriesGroupKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var seriesId = request.GroupKey.Substring(SeriesGroupKeyPrefix.Length);
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
            return ("No combinable folder group matches the request. The library may have changed; please reload.", null);
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
                    return ("One or more source directories are outside the allowed directory.", null);
                }
                if (string.Equals(srcFull, destination, StringComparison.Ordinal))
                {
                    continue;
                }
                var folder = group.Folders.FirstOrDefault(f => string.Equals(f.Directory, srcFull, StringComparison.Ordinal));
                if (folder == null)
                {
                    return ($"Source directory '{src}' is not part of this group.", null);
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
            return ("No source folders were selected for the combine operation.", null);
        }

        // Verify destination belongs to the group.
        if (!group.Folders.Any(f => string.Equals(f.Directory, destination, StringComparison.Ordinal)))
        {
            return ("Destination directory is not part of this group.", null);
        }

        // Build the move plan, handling name collisions by appending a numeric suffix.
        var moves = new List<CombineFoldersMove>();
        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (System.IO.Directory.Exists(destination))
        {
            foreach (var existing in System.IO.Directory.EnumerateFiles(destination))
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
                var destPath = Path.Combine(destination, finalName);
                // Use an ordinal (case-sensitive) comparison so we don't
                // skip moves between two folders that differ only by
                // capitalization on case-sensitive filesystems.
                var skipped = string.Equals(sourcePath, destPath, StringComparison.Ordinal);
                moves.Add(new CombineFoldersMove
                {
                    SourcePath = sourcePath,
                    DestinationPath = destPath,
                    HadConflict = hadConflict,
                    Skipped = skipped,
                    Reason = skipped ? "Source and destination are the same file." : null
                });
            }
        }

        return (null, new CombineFoldersPlan
        {
            Destination = destination,
            SourceDirectories = sourceFolders.Select(f => f.Directory).ToList(),
            Moves = moves
        });
    }

    /// <summary>
    /// Returns true if the directory (or any of its subdirectories) still
    /// contains at least one comic archive. Used by the folder-combine flow to
    /// decide whether a source directory can be safely deleted once its comics
    /// have been moved out; leftover non-comic files (cover images,
    /// ComicInfo.xml, etc.) do not block removal.
    /// </summary>
    private static bool DirectoryContainsComicArchives(string directory)
    {
        return System.IO.Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Any(ComicFileExtensions.IsComicArchive);
    }

    private static bool DirectoryContainsSuwayomiStagingFiles(string directory)
    {
        return System.IO.Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Any(path => string.Equals(Path.GetExtension(path), ".suwayomi-import", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns a map of source-file path -> existing in-memory Series metadata for
    /// the supplied paths, captured *before* a folder-combine move so the values
    /// survive the rename.
    /// </summary>
    private async Task<Dictionary<string, string>> GetPreMoveSeriesNamesAsync(
        IEnumerable<string> sourcePaths,
        CancellationToken cancellationToken)
    {
        var pathSet = new HashSet<string>(sourcePaths, StringComparer.OrdinalIgnoreCase);
        if (pathSet.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var files = await _fileStore.GetAllFilesAsync(cancellationToken);
            if (files == null)
            {
                return result;
            }

            foreach (var file in files)
            {
                if (string.IsNullOrWhiteSpace(file.FilePath)) continue;
                if (!pathSet.Contains(file.FilePath)) continue;
                var series = file.Metadata?.Series;
                if (!string.IsNullOrWhiteSpace(series))
                {
                    result[file.FilePath] = series!.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to capture pre-move series metadata for folder combine");
        }
        return result;
    }

    /// <summary>
    /// Enumerates the absolute file paths of every tracked file whose parent
    /// directory matches <paramref name="directory"/> (case-insensitive,
    /// trailing-separator tolerant). Used by the folder-combine flow to scoop
    /// up the destination folder's pre-existing files when queuing the post-
    /// combine normalize-and-rename batch.
    /// </summary>
    private async Task<List<string>> GetFilesInDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(directory))
        {
            return result;
        }

        var normalizedDir = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var files = await _fileStore.GetAllFilesAsync(cancellationToken);
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

    public class CombineFoldersRequest
    {
        public string? GroupKey { get; set; }
        public string DestinationDirectory { get; set; } = string.Empty;
        public List<string>? SourceDirectories { get; set; }
    }

    private sealed class CombineFoldersMove
    {
        public string SourcePath { get; set; } = string.Empty;
        public string DestinationPath { get; set; } = string.Empty;
        public bool HadConflict { get; set; }
        public bool Skipped { get; set; }
        public string? Reason { get; set; }
    }

    private sealed class CombineFoldersPlan
    {
        public string Destination { get; set; } = string.Empty;
        public List<string> SourceDirectories { get; set; } = new();
        public List<CombineFoldersMove> Moves { get; set; } = new();
    }
}
