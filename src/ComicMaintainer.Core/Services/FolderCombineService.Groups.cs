using System.Text.RegularExpressions;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

public sealed partial class FolderCombineService
{
    // Whitelist of parenthetical suffixes that are considered
    // non-distinguishing (i.e. a folder that differs from another only by
    // this suffix is likely the same series). Kept intentionally narrow to
    // avoid false-positive merges (e.g. year suffixes like "(2018)" are NOT
    // included because they often represent distinct print runs).
    private static readonly HashSet<string> NonDistinguishingSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "official",
        "webtoon",
        "manga",
        "manhwa",
        "manhua",
        "colorized",
        "colored",
        "colour",
        "coloured",
        "digital",
        "remastered",
        "complete",
        "omnibus",
        "tpb",
    };

    private static readonly Regex ParentheticalSuffixPattern = new(
        @"\s*\(([^()]+)\)\s*$",
        RegexOptions.Compiled);

    private async Task<List<CombinableFolderGroup>> BuildCombinableFolderGroupsAsync(CancellationToken cancellationToken = default)
    {
        var files = ((await _fileStore.GetAllFilesAsync(cancellationToken)) ?? Enumerable.Empty<ComicFile>())
            .Where(file => !string.IsNullOrWhiteSpace(file.FilePath))
            .ToList();

        if (files.Count < 2)
        {
            return new List<CombinableFolderGroup>();
        }

        // Build a directory-level index of every tracked file. This is reused
        // by the series-library and parenthetical-suffix passes below so they
        // can compute CombinableFolder entries for directories that weren't
        // bucketed by the alias-index pass (e.g. single-folder series).
        // The directory bucket uses an ordinal (case-sensitive) comparer for
        // the same reason as the alias-index pass: two on-disk folders that
        // differ only by capitalization on case-sensitive filesystems are
        // genuinely distinct directories.
        var filesByDirectory = new Dictionary<string, List<(ComicFile File, DateTime AddedAt)>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var directory = DirectoryOf(file);
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            if (!filesByDirectory.TryGetValue(directory, out var dirFiles))
            {
                dirFiles = new List<(ComicFile, DateTime)>();
                filesByDirectory[directory] = dirFiles;
            }
            dirFiles.Add((file, AddedAt(file)));
        }

        // Load the persistent series-metadata cache so we can collapse folders
        // that share canonical/provider/user aliases into a single combinable
        // group. Without this, two folders that resolve to the same series via
        // an alias (e.g. "Batman" and "The Dark Knight") would be reported as
        // distinct groups even though the user has explicitly told us they are
        // the same series.
        var aliasIndex = await BuildFolderCombineAliasIndexAsync(cancellationToken);

        // Bucket files by (groupKey -> directory -> list of files). See above
        // for why the inner dictionary is case-sensitive.
        var byGroup = new Dictionary<string, Dictionary<string, List<(ComicFile File, DateTime AddedAt)>>>(StringComparer.OrdinalIgnoreCase);
        var groupDisplay = new Dictionary<string, (string SeriesName, string? Volume)>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var directory = DirectoryOf(file);
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var groupKey = BuildFolderCombineGroupKey(file, aliasIndex);
            if (string.IsNullOrWhiteSpace(groupKey))
            {
                continue;
            }

            if (!byGroup.TryGetValue(groupKey, out var dirMap))
            {
                dirMap = new Dictionary<string, List<(ComicFile, DateTime)>>(StringComparer.Ordinal);
                byGroup[groupKey] = dirMap;
                groupDisplay[groupKey] = (
                    BuildFolderCombineSeriesDisplayName(file, aliasIndex) ?? groupKey,
                    string.IsNullOrWhiteSpace(file.Metadata?.Volume) ? null : file.Metadata!.Volume!.Trim());
            }

            if (!dirMap.TryGetValue(directory, out var dirFiles))
            {
                dirFiles = new List<(ComicFile, DateTime)>();
                dirMap[directory] = dirFiles;
            }

            dirFiles.Add((file, AddedAt(file)));
        }

        var result = new List<CombinableFolderGroup>();
        foreach (var (groupKey, dirMap) in byGroup)
        {
            if (dirMap.Count < 2)
            {
                continue;
            }

            var folders = dirMap
                .Select(kvp => BuildCombinableFolder(kvp.Key, kvp.Value))
                .OrderByDescending(f => f.NewestFileAddedAt)
                .ThenBy(f => f.Directory, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var suggested = folders[0];
            var display = groupDisplay[groupKey];
            result.Add(new CombinableFolderGroup
            {
                GroupKey = groupKey,
                SeriesName = display.SeriesName,
                Volume = display.Volume,
                Folders = folders,
                SuggestedDestinationDirectory = suggested.Directory,
                SuggestionReason = $"Contains the most recently added file ({suggested.NewestFileAddedAt:yyyy-MM-dd}).",
                TotalFileCount = folders.Sum(f => f.FileCount)
            });
        }

        // Pass 2: reconcile with the authoritative series-library grouping.
        // Any series that the SeriesLibraryService groups into a single card
        // but the alias-index pass split across multiple groups (or never
        // recognized as combinable at all) gets a synthetic `series:<id>`
        // group so the user can merge from the per-series UI.
        await ExtendWithSeriesLibraryGroupsAsync(result, filesByDirectory, cancellationToken);

        // Pass 3: surface folder pairs that differ only by a non-distinguishing
        // parenthetical suffix (e.g. "Tomb Raider King" vs
        // "Tomb Raider King (Official)").
        ExtendWithParentheticalSuffixGroups(result, filesByDirectory);

        return result
            .OrderByDescending(g => g.Folders.Max(f => f.NewestFileAddedAt))
            .ThenBy(g => g.SeriesName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? DirectoryOf(ComicFile file) =>
        !string.IsNullOrWhiteSpace(file.Directory)
            ? file.Directory
            : Path.GetDirectoryName(file.FilePath);

    /// <summary>
    /// When the file was added to the library: the file store's CreatedAt,
    /// or its modification time for a file the store has no date for.
    /// </summary>
    private static DateTime AddedAt(ComicFile file) =>
        file.CreatedAt != default ? file.CreatedAt : file.LastModified;

    private static CombinableFolder BuildCombinableFolder(
        string directory,
        IReadOnlyList<(ComicFile File, DateTime AddedAt)> dirFiles)
    {
        var newestAdded = dirFiles.Max(e => e.AddedAt);
        var oldestAdded = dirFiles.Min(e => e.AddedAt);
        var newestModified = dirFiles.Max(e => e.File.LastModified);
        var totalSize = dirFiles.Sum(e => e.File.FileSize);
        var sample = dirFiles
            .OrderBy(e => e.File.FileName, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(e => e.File.FileName)
            .ToList();
        var allFilePaths = dirFiles
            .Select(e => e.File.FilePath)
            .ToList();
        return new CombinableFolder
        {
            Directory = directory,
            FileCount = dirFiles.Count,
            TotalSize = totalSize,
            NewestFileAddedAt = newestAdded,
            OldestFileAddedAt = oldestAdded,
            NewestFileModifiedAt = newestModified,
            SampleFileNames = sample,
            FilePaths = allFilePaths
        };
    }

    /// <summary>
    /// Pass that uses the authoritative <see cref="ISeriesLibraryService"/>
    /// grouping to reconcile combinable folder groups. For every series that
    /// the library reports as spanning >= 2 distinct directories, we ensure
    /// a single combinable group exists keyed <c>series:&lt;seriesId&gt;</c>
    /// that contains every one of those directories. Any pre-existing
    /// combinable groups that overlap with the series are merged into the
    /// synthetic group.
    /// </summary>
    private async Task ExtendWithSeriesLibraryGroupsAsync(
        List<CombinableFolderGroup> groups,
        IReadOnlyDictionary<string, List<(ComicFile File, DateTime AddedAt)>> filesByDirectory,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SeriesFoldersResult> seriesGroups;
        try
        {
            seriesGroups = await _seriesLibrary.GetAllSeriesFolderGroupsAsync(filter: null, cancellationToken)
                ?? Array.Empty<SeriesFoldersResult>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Failed to load series-library folder groups for combinable-folder reconciliation");
            return;
        }

        foreach (var series in seriesGroups)
        {
            if (series.Folders.Count < 2)
            {
                continue;
            }

            // Resolve every series-library directory back to its tracked-file
            // bucket. We skip the series entirely if none of the directories
            // have any tracked files (defensive — shouldn't happen).
            var directoryFiles = series.Folders
                .Select(folder => folder.Directory)
                .Where(directory => HasTrackedFiles(filesByDirectory, directory))
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(directory => directory, directory => filesByDirectory[directory], StringComparer.Ordinal);

            if (directoryFiles.Count < 2)
            {
                continue;
            }

            var synthGroupKey = CombinableFolderGroup.SeriesGroupKeyPrefix + series.Id;

            // Find every existing combinable group that overlaps with these
            // directories. Comparison is ordinal because the directories
            // produced by both code paths are absolute paths from the file
            // store (case-sensitive on Linux/Docker bind mounts).
            var overlapping = groups
                .Where(g => g.Folders.Any(f => directoryFiles.ContainsKey(f.Directory)))
                .ToList();

            // Build the merged folder set: include every directory in the
            // series-library view plus any directories already attached to an
            // overlapping group (those may be additional folders the alias
            // index discovered).
            var mergedDirectoryFiles = new Dictionary<string, List<(ComicFile File, DateTime AddedAt)>>(directoryFiles, StringComparer.Ordinal);
            AddTrackedDirectories(mergedDirectoryFiles, overlapping, filesByDirectory);

            // If the only overlap is a single group whose folders are exactly
            // this series's folders (no extras, none missing), leave it alone
            // to preserve the original group key / reason / display name.
            if (overlapping.Count == 1
                && overlapping[0].Folders.Count == mergedDirectoryFiles.Count
                && overlapping[0].Folders.All(f => mergedDirectoryFiles.ContainsKey(f.Directory)))
            {
                continue;
            }

            // Remove every overlapping group; we're about to replace them
            // with a single synthetic series group.
            foreach (var overlap in overlapping)
            {
                groups.Remove(overlap);
            }

            var mergedFolders = mergedDirectoryFiles
                .Select(kvp => BuildCombinableFolder(kvp.Key, kvp.Value))
                .OrderByDescending(f => f.NewestFileAddedAt)
                .ThenBy(f => f.Directory, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var suggested = mergedFolders[0];
            var seriesName = !string.IsNullOrWhiteSpace(series.Title)
                ? series.Title
                : (overlapping.FirstOrDefault()?.SeriesName ?? series.Id);

            groups.Add(new CombinableFolderGroup
            {
                GroupKey = synthGroupKey,
                SeriesName = seriesName,
                Volume = null,
                Folders = mergedFolders,
                SuggestedDestinationDirectory = suggested.Directory,
                SuggestionReason = overlapping.Count == 0
                    ? "These folders are all attributed to the same series."
                    : $"Contains the most recently added file ({suggested.NewestFileAddedAt:yyyy-MM-dd}).",
                TotalFileCount = mergedFolders.Sum(f => f.FileCount)
            });
        }
    }

    private static bool HasTrackedFiles(
        IReadOnlyDictionary<string, List<(ComicFile File, DateTime AddedAt)>> filesByDirectory,
        string directory) =>
        !string.IsNullOrWhiteSpace(directory)
        && filesByDirectory.TryGetValue(directory, out var files)
        && files.Count > 0;

    /// <summary>
    /// Adds every directory of <paramref name="groups"/> that has tracked files
    /// and is not already in <paramref name="directoryFiles"/>.
    /// </summary>
    private static void AddTrackedDirectories(
        Dictionary<string, List<(ComicFile File, DateTime AddedAt)>> directoryFiles,
        IEnumerable<CombinableFolderGroup> groups,
        IReadOnlyDictionary<string, List<(ComicFile File, DateTime AddedAt)>> filesByDirectory)
    {
        var directories = groups
            .SelectMany(group => group.Folders)
            .Select(folder => folder.Directory)
            .Where(directory => HasTrackedFiles(filesByDirectory, directory));
        foreach (var directory in directories)
        {
            directoryFiles.TryAdd(directory, filesByDirectory[directory]);
        }
    }

    /// <summary>
    /// Returns the "base" series name with any trailing whitelisted
    /// parenthetical suffix stripped, e.g.
    /// "Tomb Raider King (Official)" -> "Tomb Raider King".
    /// Returns null when there is no whitelisted suffix (so callers can keep
    /// the original name unchanged).
    /// </summary>
    private static string? TryStripWhitelistedParentheticalSuffix(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return null;
        }

        var match = ParentheticalSuffixPattern.Match(folderName);
        if (!match.Success)
        {
            return null;
        }

        var inside = match.Groups[1].Value.Trim();
        if (string.IsNullOrEmpty(inside))
        {
            return null;
        }

        if (!NonDistinguishingSuffixes.Contains(inside))
        {
            return null;
        }

        var stripped = folderName.Substring(0, match.Index).Trim();
        return string.IsNullOrEmpty(stripped) ? null : stripped;
    }

    /// <summary>
    /// Pass that surfaces folder pairs differing only by a non-distinguishing
    /// parenthetical suffix from <see cref="NonDistinguishingSuffixes"/>. The
    /// match key is the folder name itself (case-insensitive) rather than the
    /// in-archive metadata series name, because the canonical use case is
    /// folders like "Tomb Raider King" vs "Tomb Raider King (Official)" that
    /// the library has not yet learned share a series.
    /// </summary>
    private static void ExtendWithParentheticalSuffixGroups(
        List<CombinableFolderGroup> groups,
        IReadOnlyDictionary<string, List<(ComicFile File, DateTime AddedAt)>> filesByDirectory)
    {
        // Bucket every tracked directory by its normalized "base" folder name
        // (folder name with whitelisted parenthetical suffix stripped). When
        // two or more directories share a base name AND at least one of them
        // had the suffix originally, they're candidates to merge.
        var byBaseName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var hadSuffixByDirectory = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var directory in filesByDirectory.Keys)
        {
            var folderName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(folderName))
            {
                continue;
            }

            var stripped = TryStripWhitelistedParentheticalSuffix(folderName);
            var baseName = stripped ?? folderName;
            hadSuffixByDirectory[directory] = stripped is not null;

            var normalized = SeriesKey.TryNormalize(baseName);
            if (string.IsNullOrWhiteSpace(normalized) || IsAmbiguousNormalizedKey(normalized))
            {
                continue;
            }

            if (!byBaseName.TryGetValue(normalized, out var list))
            {
                list = new List<string>();
                byBaseName[normalized] = list;
            }
            list.Add(directory);
        }

        foreach (var (_, directories) in byBaseName)
        {
            if (directories.Count < 2)
            {
                continue;
            }

            // At least one directory in the bucket must have actually had a
            // whitelisted suffix (otherwise this is just two folders that
            // share a name with no whitelist trigger — those should already
            // be picked up by earlier passes or stay separate).
            if (!directories.Any(d => hadSuffixByDirectory.TryGetValue(d, out var had) && had))
            {
                continue;
            }

            // If every directory in the bucket is already covered by a single
            // existing combinable group, leave that group alone.
            var coveringGroups = groups
                .Where(g => g.Folders.Any(f => directories.Contains(f.Directory, StringComparer.Ordinal)))
                .ToList();

            // Merge the directories with any overlapping groups' directories.
            var mergedDirectoryFiles = new Dictionary<string, List<(ComicFile File, DateTime AddedAt)>>(StringComparer.Ordinal);
            foreach (var dir in directories.Where(dir => HasTrackedFiles(filesByDirectory, dir)))
            {
                mergedDirectoryFiles[dir] = filesByDirectory[dir];
            }
            AddTrackedDirectories(mergedDirectoryFiles, coveringGroups, filesByDirectory);

            if (mergedDirectoryFiles.Count < 2)
            {
                continue;
            }

            // If a single existing group already covers exactly this set, no
            // change is needed.
            if (coveringGroups.Count == 1
                && coveringGroups[0].Folders.Count == mergedDirectoryFiles.Count
                && coveringGroups[0].Folders.All(f => mergedDirectoryFiles.ContainsKey(f.Directory)))
            {
                continue;
            }

            foreach (var overlap in coveringGroups)
            {
                groups.Remove(overlap);
            }

            var mergedFolders = mergedDirectoryFiles
                .Select(kvp => BuildCombinableFolder(kvp.Key, kvp.Value))
                .OrderByDescending(f => f.NewestFileAddedAt)
                .ThenBy(f => f.Directory, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var suggested = mergedFolders[0];

            // Surface the suffix that triggered the merge in the suggestion
            // reason so the user can tell at a glance why the system is
            // recommending this combine.
            var triggerFolderName = directories
                .Select(d => Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                .FirstOrDefault(name => TryStripWhitelistedParentheticalSuffix(name ?? string.Empty) is not null)
                ?? string.Empty;
            var triggerMatch = ParentheticalSuffixPattern.Match(triggerFolderName);
            var suffixDisplay = triggerMatch.Success ? $"({triggerMatch.Groups[1].Value.Trim()})" : "(suffix)";
            var baseFolderName = Path.GetFileName(directories
                .OrderBy(d => Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.Length ?? 0)
                .First()
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? string.Empty;

            var groupKey = "suffix:" + (SeriesKey.TryNormalize(baseFolderName) ?? baseFolderName);

            groups.Add(new CombinableFolderGroup
            {
                GroupKey = groupKey,
                SeriesName = baseFolderName,
                Volume = null,
                Folders = mergedFolders,
                SuggestedDestinationDirectory = suggested.Directory,
                SuggestionReason = $"Folder name differs only by a non-distinguishing suffix: {suffixDisplay}.",
                TotalFileCount = mergedFolders.Sum(f => f.FileCount)
            });
        }
    }

    /// <summary>
    /// Builds a synthetic combinable folder group for the given series id by
    /// asking the authoritative <see cref="ISeriesLibraryService"/> which
    /// folders contribute to the series, then materialising
    /// <see cref="CombinableFolder"/> entries from the file store. Returns
    /// null when the series is unknown or has fewer than two folders.
    /// </summary>
    private async Task<CombinableFolderGroup?> BuildSeriesScopedCombinableGroupAsync(
        string seriesId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            return null;
        }

        SeriesFoldersResult? seriesFolders;
        try
        {
            seriesFolders = await _seriesLibrary.GetFoldersForSeriesIdAsync(seriesId, filter: null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Failed to resolve series-scoped folders for {SeriesId}",
                LoggingHelper.SanitizeForLog(seriesId));
            return null;
        }

        if (seriesFolders is null || seriesFolders.Folders.Count < 2)
        {
            return null;
        }

        var files = ((await _fileStore.GetAllFilesAsync(cancellationToken)) ?? Enumerable.Empty<ComicFile>())
            .Where(file => !string.IsNullOrWhiteSpace(file.FilePath))
            .ToList();
        if (files.Count == 0)
        {
            return null;
        }

        var seriesDirSet = new HashSet<string>(
            seriesFolders.Folders.Select(f => f.Directory),
            StringComparer.Ordinal);

        var filesByDirectory = new Dictionary<string, List<(ComicFile File, DateTime AddedAt)>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var directory = DirectoryOf(file);
            if (string.IsNullOrWhiteSpace(directory) || !seriesDirSet.Contains(directory))
            {
                continue;
            }

            if (!filesByDirectory.TryGetValue(directory, out var dirFiles))
            {
                dirFiles = new List<(ComicFile, DateTime)>();
                filesByDirectory[directory] = dirFiles;
            }
            dirFiles.Add((file, AddedAt(file)));
        }

        if (filesByDirectory.Count < 2)
        {
            return null;
        }

        var folders = filesByDirectory
            .Select(kvp => BuildCombinableFolder(kvp.Key, kvp.Value))
            .OrderByDescending(f => f.NewestFileAddedAt)
            .ThenBy(f => f.Directory, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var suggested = folders[0];
        return new CombinableFolderGroup
        {
            GroupKey = CombinableFolderGroup.SeriesGroupKeyPrefix + seriesId,
            SeriesName = !string.IsNullOrWhiteSpace(seriesFolders.Title) ? seriesFolders.Title : seriesId,
            Volume = null,
            Folders = folders,
            SuggestedDestinationDirectory = suggested.Directory,
            SuggestionReason = "These folders are all attributed to the same series.",
            TotalFileCount = folders.Sum(f => f.FileCount)
        };
    }
}
