using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Text.RegularExpressions;

namespace ComicMaintainer.WebApi.Controllers;

public partial class FilesController
{
    private const string CombinableGroupsCacheKey = "FilesController.CombinableFolderGroups";
    private static readonly TimeSpan CombinableGroupsCacheMaxAge = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim CombinableGroupsBuildLock = new(1, 1);

    private sealed record CombinableGroupsCacheEntry(
        (long Files, long Metadata, string? Language) Versions,
        IReadOnlyList<CombinableFolderGroup> Groups);

    /// <summary>
    /// Read-only view of the combinable-folder groups for display endpoints
    /// (header counts, the combinable-folders list, per-series folder info).
    /// The full rebuild scans every tracked file plus the metadata cache, so
    /// the result is cached until the file store or metadata cache reports a
    /// change. Mutating flows (the combine plan) still call
    /// <see cref="BuildCombinableFolderGroupsAsync"/> directly so they always
    /// act on fresh data. Callers must not modify the returned groups.
    /// </summary>
    private async Task<IReadOnlyList<CombinableFolderGroup>> GetCombinableFolderGroupsCachedAsync(CancellationToken cancellationToken = default)
    {
        if (_memoryCache is null
            || _fileStore is not IChangeVersionSource fileVersions
            || _metadataCache is not IChangeVersionSource metadataVersions)
        {
            return await BuildCombinableFolderGroupsAsync(cancellationToken);
        }

        (long, long, string?) CaptureVersions() =>
            (fileVersions.ChangeVersion, metadataVersions.ChangeVersion, _settings.CurrentValue?.DefaultPreferredLanguage);

        // Capture versions before building so a concurrent write marks the
        // stored entry stale rather than being silently lost.
        var versions = CaptureVersions();
        if (_memoryCache.TryGetValue(CombinableGroupsCacheKey, out CombinableGroupsCacheEntry? cached)
            && cached is not null && cached.Versions.Equals(versions))
        {
            return cached.Groups;
        }

        await CombinableGroupsBuildLock.WaitAsync(cancellationToken);
        try
        {
            versions = CaptureVersions();
            if (_memoryCache.TryGetValue(CombinableGroupsCacheKey, out cached)
                && cached is not null && cached.Versions.Equals(versions))
            {
                return cached.Groups;
            }

            var groups = await BuildCombinableFolderGroupsAsync(cancellationToken);
            _memoryCache.Set(
                CombinableGroupsCacheKey,
                new CombinableGroupsCacheEntry(versions, groups),
                CombinableGroupsCacheMaxAge);
            return groups;
        }
        finally
        {
            CombinableGroupsBuildLock.Release();
        }
    }

    private async Task<int> GetCombinableFolderCountAsync(CancellationToken cancellationToken = default)
    {
        var groups = await GetCombinableFolderGroupsCachedAsync(cancellationToken);
        // Mirror the previous behavior: every folder beyond the suggested destination counts as a source.
        return groups.Sum(g => Math.Max(0, g.Folders.Count - 1));
    }

    private async Task<List<CombinableFolderGroup>> BuildCombinableFolderGroupsAsync(CancellationToken cancellationToken = default)
    {
        var files = ((await _fileStore.GetAllFilesAsync(cancellationToken)) ?? Enumerable.Empty<ComicFile>())
            .Where(file => !string.IsNullOrWhiteSpace(file.FilePath))
            .ToList();

        if (files.Count < 2)
        {
            return new List<CombinableFolderGroup>();
        }

        var addedAtLookup = await GetAddedAtLookupAsync(cancellationToken);

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
            var directory = !string.IsNullOrWhiteSpace(file.Directory)
                ? file.Directory
                : Path.GetDirectoryName(file.FilePath);

            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var addedAt = addedAtLookup.TryGetValue(file.FilePath, out var createdAt)
                ? createdAt
                : file.LastModified;

            if (!filesByDirectory.TryGetValue(directory, out var dirFiles))
            {
                dirFiles = new List<(ComicFile, DateTime)>();
                filesByDirectory[directory] = dirFiles;
            }
            dirFiles.Add((file, addedAt));
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
            var directory = !string.IsNullOrWhiteSpace(file.Directory)
                ? file.Directory
                : Path.GetDirectoryName(file.FilePath);

            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var groupKey = BuildFolderCombineGroupKey(file, aliasIndex);
            if (string.IsNullOrWhiteSpace(groupKey))
            {
                continue;
            }

            var addedAt = addedAtLookup.TryGetValue(file.FilePath, out var createdAt)
                ? createdAt
                : file.LastModified;

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

            dirFiles.Add((file, addedAt));
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

        // Pass 2 (Change A): reconcile with the authoritative series-library
        // grouping. Any series that the SeriesLibraryService groups into a
        // single card but the alias-index pass split across multiple groups
        // (or never recognized as combinable at all) gets a synthetic
        // `series:<id>` group so the user can merge from the per-series UI.
        await ExtendWithSeriesLibraryGroupsAsync(result, filesByDirectory, cancellationToken);

        // Pass 3 (Change B): surface folder pairs that differ only by a
        // non-distinguishing parenthetical suffix (e.g. "Tomb Raider King" vs
        // "Tomb Raider King (Official)").
        ExtendWithParentheticalSuffixGroups(result, filesByDirectory);

        return result
            .OrderByDescending(g => g.Folders.Max(f => f.NewestFileAddedAt))
            .ThenBy(g => g.SeriesName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

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
    internal const string SeriesGroupKeyPrefix = "series:";

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
            var directoryFiles = new Dictionary<string, List<(ComicFile File, DateTime AddedAt)>>(StringComparer.Ordinal);
            foreach (var folder in series.Folders)
            {
                if (string.IsNullOrWhiteSpace(folder.Directory)) continue;
                if (filesByDirectory.TryGetValue(folder.Directory, out var dirFiles) && dirFiles.Count > 0)
                {
                    directoryFiles[folder.Directory] = dirFiles;
                }
            }

            if (directoryFiles.Count < 2)
            {
                continue;
            }

            var synthGroupKey = SeriesGroupKeyPrefix + series.Id;

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
            foreach (var overlap in overlapping)
            {
                foreach (var folder in overlap.Folders)
                {
                    if (string.IsNullOrWhiteSpace(folder.Directory)) continue;
                    if (mergedDirectoryFiles.ContainsKey(folder.Directory)) continue;
                    if (filesByDirectory.TryGetValue(folder.Directory, out var dirFiles) && dirFiles.Count > 0)
                    {
                        mergedDirectoryFiles[folder.Directory] = dirFiles;
                    }
                }
            }

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
    private void ExtendWithParentheticalSuffixGroups(
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

            var normalized = NormalizeFolderCombineKey(baseName);
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
            foreach (var dir in directories)
            {
                if (filesByDirectory.TryGetValue(dir, out var dirFiles) && dirFiles.Count > 0)
                {
                    mergedDirectoryFiles[dir] = dirFiles;
                }
            }
            foreach (var overlap in coveringGroups)
            {
                foreach (var folder in overlap.Folders)
                {
                    if (string.IsNullOrWhiteSpace(folder.Directory)) continue;
                    if (mergedDirectoryFiles.ContainsKey(folder.Directory)) continue;
                    if (filesByDirectory.TryGetValue(folder.Directory, out var dirFiles) && dirFiles.Count > 0)
                    {
                        mergedDirectoryFiles[folder.Directory] = dirFiles;
                    }
                }
            }

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

            var groupKey = "suffix:" + (NormalizeFolderCombineKey(baseFolderName) ?? baseFolderName);

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

    [HttpGet("combinable-folders")]
    public async Task<ActionResult<object>> GetCombinableFolders(CancellationToken cancellationToken = default)
    {
        try
        {
            var groups = await GetCombinableFolderGroupsCachedAsync(cancellationToken);
            var dtos = groups.Select(ToCombinableFolderGroupDto).ToList();
            return Ok(new { groups = dtos });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving combinable folders");
            return StatusCode(500, "Error retrieving combinable folders");
        }
    }

    private sealed class CombinableFolder
    {
        public string Directory { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public long TotalSize { get; set; }
        public DateTime NewestFileAddedAt { get; set; }
        public DateTime OldestFileAddedAt { get; set; }
        public DateTime NewestFileModifiedAt { get; set; }
        public List<string> SampleFileNames { get; set; } = new();
        public List<string> FilePaths { get; set; } = new();
    }

    private sealed class CombinableFolderGroup
    {
        public string GroupKey { get; set; } = string.Empty;
        public string SeriesName { get; set; } = string.Empty;
        public string? Volume { get; set; }
        public int TotalFileCount { get; set; }
        public string SuggestedDestinationDirectory { get; set; } = string.Empty;
        public string SuggestionReason { get; set; } = string.Empty;
        public List<CombinableFolder> Folders { get; set; } = new();
    }

    public class CombinableFolderDto
    {
        public string Directory { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public long TotalSize { get; set; }
        public DateTime NewestFileAddedAt { get; set; }
        public DateTime OldestFileAddedAt { get; set; }
        public DateTime NewestFileModifiedAt { get; set; }
        public List<string> SampleFileNames { get; set; } = new();
        public bool IsSuggested { get; set; }
    }

    public class CombinableFolderGroupDto
    {
        public string GroupKey { get; set; } = string.Empty;
        public string SeriesName { get; set; } = string.Empty;
        public string? Volume { get; set; }
        public int TotalFileCount { get; set; }
        public string SuggestedDestinationDirectory { get; set; } = string.Empty;
        public string SuggestionReason { get; set; } = string.Empty;
        public List<CombinableFolderDto> Folders { get; set; } = new();
    }
}
