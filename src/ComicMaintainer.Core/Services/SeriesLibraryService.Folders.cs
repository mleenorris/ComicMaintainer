using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

public partial class SeriesLibraryService
{
    public async Task<IReadOnlyList<string>> GetTitlesForSeriesIdAsync(
        string seriesId,
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            return Array.Empty<string>();
        }

        var (fileFilter, _) = SplitFilter(filter);
        var groups = await BuildGroupsAsync(fileFilter, allowDiskRead: false, cancellationToken);
        if (!TryResolveGroup(groups, seriesId, out var accumulator))
        {
            return Array.Empty<string>();
        }

        var titles = new List<string>();
        if (!string.IsNullOrWhiteSpace(accumulator.CanonicalTitle))
        {
            titles.Add(accumulator.CanonicalTitle);
        }
        foreach (var alias in accumulator.Aliases)
        {
            if (!string.IsNullOrWhiteSpace(alias) &&
                !titles.Contains(alias, StringComparer.OrdinalIgnoreCase))
            {
                titles.Add(alias);
            }
        }
        return titles;
    }

    public async Task<SeriesFoldersResult?> GetFoldersForSeriesIdAsync(
        string seriesId,
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            return null;
        }

        var (fileFilter, _) = SplitFilter(filter);
        var groups = await BuildGroupsAsync(fileFilter, allowDiskRead: false, cancellationToken);
        if (!TryResolveGroup(groups, seriesId, out var accumulator))
        {
            return null;
        }

        // Bucket the series's issues by their parent directory. We use an
        // ordinal (case-sensitive) comparer because on case-sensitive
        // filesystems (Linux/Docker bind mounts) two folders that differ only
        // by capitalisation are genuinely distinct on disk, and the user
        // should see both so they can decide whether to combine them.
        var byDirectory = new Dictionary<string, (int Count, long Size)>(StringComparer.Ordinal);
        foreach (var issue in accumulator.Issues)
        {
            if (string.IsNullOrWhiteSpace(issue.FilePath))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(issue.FilePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            if (byDirectory.TryGetValue(directory, out var existing))
            {
                byDirectory[directory] = (existing.Count + 1, existing.Size + issue.Size);
            }
            else
            {
                byDirectory[directory] = (1, issue.Size);
            }
        }

        var folders = byDirectory
            .Select(kvp => new SeriesFolderDto
            {
                Directory = kvp.Key,
                FileCount = kvp.Value.Count,
                TotalSize = kvp.Value.Size
            })
            .OrderByDescending(folder => folder.FileCount)
            .ThenBy(folder => folder.Directory, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SeriesFoldersResult
        {
            Id = accumulator.Id,
            Title = accumulator.DisplayTitle,
            Folders = folders
        };
    }

    public async Task<IReadOnlyList<SeriesFoldersResult>> GetAllSeriesFolderGroupsAsync(
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        var (fileFilter, _) = SplitFilter(filter);
        var groups = await BuildGroupsAsync(fileFilter, allowDiskRead: false, cancellationToken);

        var results = new List<SeriesFoldersResult>(groups.Count);
        foreach (var accumulator in groups.Values)
        {
            // Same per-directory bucketing as GetFoldersForSeriesIdAsync; use
            // an ordinal comparer so case-distinct on-disk folders remain
            // separate entries on case-sensitive filesystems.
            var byDirectory = new Dictionary<string, (int Count, long Size)>(StringComparer.Ordinal);
            foreach (var issue in accumulator.Issues)
            {
                if (string.IsNullOrWhiteSpace(issue.FilePath))
                {
                    continue;
                }

                var directory = Path.GetDirectoryName(issue.FilePath);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                if (byDirectory.TryGetValue(directory, out var existing))
                {
                    byDirectory[directory] = (existing.Count + 1, existing.Size + issue.Size);
                }
                else
                {
                    byDirectory[directory] = (1, issue.Size);
                }
            }

            var folders = byDirectory
                .Select(kvp => new SeriesFolderDto
                {
                    Directory = kvp.Key,
                    FileCount = kvp.Value.Count,
                    TotalSize = kvp.Value.Size
                })
                .OrderByDescending(folder => folder.FileCount)
                .ThenBy(folder => folder.Directory, StringComparer.OrdinalIgnoreCase)
                .ToList();

            results.Add(new SeriesFoldersResult
            {
                Id = accumulator.Id,
                Title = accumulator.DisplayTitle,
                Folders = folders
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<SeriesFolderDto>> GetFoldersForNormalizedKeyAsync(
        string normalizedKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            return Array.Empty<SeriesFolderDto>();
        }

        var groups = await BuildGroupsAsync(filter: null, allowDiskRead: false, cancellationToken);

        // Match by ImageNormalizedKey first (the authoritative cache key for
        // the cover image); fall back to a case-insensitive match against the
        // accumulator id so callers that pass an unmatched series key still
        // find the right folders in libraries that haven't been linked to
        // external metadata yet.
        var byDirectory = new Dictionary<string, (int Count, long Size)>(StringComparer.Ordinal);
        foreach (var accumulator in groups.Values)
        {
            var matchesImageKey = !string.IsNullOrEmpty(accumulator.ImageNormalizedKey)
                && string.Equals(accumulator.ImageNormalizedKey, normalizedKey, StringComparison.OrdinalIgnoreCase);
            var matchesId = !matchesImageKey
                && string.Equals(accumulator.Id, normalizedKey, StringComparison.OrdinalIgnoreCase);
            if (!matchesImageKey && !matchesId)
            {
                continue;
            }

            foreach (var issue in accumulator.Issues)
            {
                if (string.IsNullOrWhiteSpace(issue.FilePath))
                {
                    continue;
                }
                var directory = Path.GetDirectoryName(issue.FilePath);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }
                if (byDirectory.TryGetValue(directory, out var existing))
                {
                    byDirectory[directory] = (existing.Count + 1, existing.Size + issue.Size);
                }
                else
                {
                    byDirectory[directory] = (1, issue.Size);
                }
            }
        }

        return byDirectory
            .Select(kvp => new SeriesFolderDto
            {
                Directory = kvp.Key,
                FileCount = kvp.Value.Count,
                TotalSize = kvp.Value.Size
            })
            .OrderByDescending(folder => folder.FileCount)
            .ThenBy(folder => folder.Directory, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string?> GetFirstIssueFilePathForNormalizedKeyAsync(
        string normalizedKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            return null;
        }

        var groups = await BuildGroupsAsync(filter: null, allowDiskRead: false, cancellationToken);

        // Match by ImageNormalizedKey first (the authoritative cache key for
        // the cover image); fall back to a case-insensitive match against the
        // accumulator id so callers that pass an unmatched series key still
        // resolve the right series. Mirrors GetFoldersForNormalizedKeyAsync.
        foreach (var accumulator in groups.Values)
        {
            var matchesImageKey = !string.IsNullOrEmpty(accumulator.ImageNormalizedKey)
                && string.Equals(accumulator.ImageNormalizedKey, normalizedKey, StringComparison.OrdinalIgnoreCase);
            var matchesId = !matchesImageKey
                && string.Equals(accumulator.Id, normalizedKey, StringComparison.OrdinalIgnoreCase);
            if (!matchesImageKey && !matchesId)
            {
                continue;
            }

            var sortedIssues = accumulator.GetSortedIssues();
            return sortedIssues
                .Select(issue => issue.FilePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        }

        return null;
    }
}
