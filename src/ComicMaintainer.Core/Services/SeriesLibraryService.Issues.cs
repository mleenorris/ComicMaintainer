using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

public partial class SeriesLibraryService
{
    public async Task<AdjacentIssueResult> GetAdjacentIssueAsync(
        string filePath,
        string direction = "next",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new AdjacentIssueResult { Found = false };
        }

        // Use the unfiltered grouping so adjacency reflects the whole series
        // regardless of any active library filter, and never reads archives
        // off disk so the reader's next/previous prefetch stays fast.
        var groupingKeyByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groups = await BuildGroupsAsync(filter: null, allowDiskRead: false, cancellationToken, groupingKeyByFile);

        var owningSeries = groups.Values.FirstOrDefault(accumulator =>
            accumulator.Issues.Any(issue =>
                string.Equals(issue.FilePath, filePath, StringComparison.Ordinal)));

        if (owningSeries is null)
        {
            return new AdjacentIssueResult { Found = false };
        }

        var sortedIssues = SortIssues(owningSeries.Issues);

        // A library card can legitimately span several distinct source series:
        // union-find merges groups through cache aliases / shared metadata, and
        // those merges are occasionally wrong. Reader navigation must never hop
        // into a different comic, so restrict the candidates to issues that were
        // grouped under the same title as the current file.
        if (groupingKeyByFile.TryGetValue(filePath, out var currentGroupingKey)
            && !string.IsNullOrWhiteSpace(currentGroupingKey))
        {
            var scoped = sortedIssues
                .Where(issue => groupingKeyByFile.TryGetValue(issue.FilePath, out var key)
                    && string.Equals(key, currentGroupingKey, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (scoped.Count > 0)
            {
                sortedIssues = scoped;
            }
        }

        var currentIndex = sortedIssues.FindIndex(issue =>
            string.Equals(issue.FilePath, filePath, StringComparison.Ordinal));

        if (currentIndex == -1)
        {
            return new AdjacentIssueResult { Found = false };
        }

        var adjacentIndex = string.Equals(direction, "next", StringComparison.OrdinalIgnoreCase)
            ? currentIndex + 1
            : currentIndex - 1;

        if (adjacentIndex < 0 || adjacentIndex >= sortedIssues.Count)
        {
            // First/last issue of the series: there is intentionally no
            // adjacent issue, so the reader shows "end of series".
            return new AdjacentIssueResult { Found = true, HasAdjacent = false };
        }

        var adjacent = sortedIssues[adjacentIndex];
        return new AdjacentIssueResult
        {
            Found = true,
            HasAdjacent = true,
            FilePath = adjacent.FilePath,
            FileName = adjacent.FileName
        };
    }

    public async Task<SeriesIssuesResult?> GetSeriesIssuesAsync(
        string seriesId,
        string? filter = null,
        int page = 1,
        int perPage = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            return null;
        }

        // Group cache-only first (cheap). If the requested id matches a group,
        // optionally upgrade just that group's metadata via disk reads to fill
        // in per-issue details that may be missing from the DB cache.
        var (fileFilter, _) = SplitFilter(filter);
        var groups = await BuildGroupsAsync(fileFilter, allowDiskRead: false, cancellationToken);
        if (!TryResolveGroup(groups, seriesId, out var accumulator))
        {
            // The requested series id is not present in the filtered groups.
            // This happens for two reasons when an active file filter is in
            // effect: (1) the filter excludes every file of the series, or
            // (2) the union-find representative used as the series id
            // "drifted" because excluding files changed which file is the
            // first key in the union. Both cases happen during ordinary
            // filter-toggle usage from the series-detail view and must NOT
            // 404 — otherwise the UI shows a "Failed to load issues" error
            // and the user is effectively kicked out of the series view.
            //
            // Resolve the series via the unfiltered groups: if found there,
            // try to map it back to an equivalent filtered group by matching
            // canonical title / aliases (id-drift); otherwise synthesize an
            // empty accumulator that preserves the series metadata so the
            // detail view stays open with an empty issues list.
            if (string.IsNullOrWhiteSpace(fileFilter))
            {
                return null;
            }

            var unfilteredGroups = await BuildGroupsAsync(filter: null, allowDiskRead: false, cancellationToken);
            if (!TryResolveGroup(unfilteredGroups, seriesId, out var unfilteredAccumulator))
            {
                return null;
            }

            accumulator = FindMatchingFilteredGroup(groups, unfilteredAccumulator)
                ?? new SeriesAccumulator
                {
                    Id = unfilteredAccumulator.Id,
                    DisplayTitle = unfilteredAccumulator.DisplayTitle,
                    CanonicalTitle = unfilteredAccumulator.CanonicalTitle,
                    MetadataSource = unfilteredAccumulator.MetadataSource,
                    LookupStatus = unfilteredAccumulator.LookupStatus,
                    LastLookupUtc = unfilteredAccumulator.LastLookupUtc,
                    Aliases = new List<string>(unfilteredAccumulator.Aliases),
                    HasExternalImage = unfilteredAccumulator.HasExternalImage,
                    ImageNormalizedKey = unfilteredAccumulator.ImageNormalizedKey,
                    Synopsis = unfilteredAccumulator.Synopsis
                    // Issues intentionally left empty: the active filter
                    // excludes every file in this series.
                };
        }

        return await BuildSeriesIssuesResultAsync(accumulator, page, perPage, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, SeriesIssuesResult>> GetSeriesIssuesForIdsAsync(
        IEnumerable<string> seriesIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seriesIds);

        var groups = await BuildGroupsAsync(filter: null, allowDiskRead: false, cancellationToken);
        var results = new Dictionary<string, SeriesIssuesResult>(StringComparer.Ordinal);
        foreach (var seriesId in seriesIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveGroup(groups, seriesId, out var accumulator))
            {
                continue;
            }

            results[seriesId] = await BuildSeriesIssuesResultAsync(
                accumulator, page: 1, perPage: -1, cancellationToken);
        }

        return results;
    }

    private async Task<SeriesIssuesResult> BuildSeriesIssuesResultAsync(
        SeriesAccumulator accumulator,
        int page,
        int perPage,
        CancellationToken cancellationToken)
    {
        // Best-effort upgrade: for issues with empty title metadata, try the
        // archive on disk. Bounded to the requested page so we never read more
        // archives than the user actually sees.
        var sortedIssues = SortIssues(accumulator.Issues);
        var totalIssues = sortedIssues.Count;
        var effectivePerPage = perPage == -1 ? totalIssues : Math.Max(1, perPage);
        var totalPages = perPage == -1 ? 1 : (int)Math.Ceiling((double)totalIssues / effectivePerPage);
        page = Math.Max(1, Math.Min(page, totalPages == 0 ? 1 : totalPages));
        var pageIssues = perPage == -1
            ? sortedIssues
            : sortedIssues.Skip((page - 1) * effectivePerPage).Take(effectivePerPage).ToList();

        // Best-effort upgrade: for issues with empty title metadata, try the
        // archive on disk. Bounded to the requested page so we never read more
        // archives than the user actually sees. An additional hard cap from
        // AppSettings.SeriesIssuesMaxArchiveUpgrades protects against
        // pathological requests (e.g. perPage == -1 with a 1000-issue series
        // and no cached metadata) where the upgrade loop would otherwise
        // dominate request latency.
        var upgradeBudget = Math.Max(0, _settings.CurrentValue?.SeriesIssuesMaxArchiveUpgrades ?? 100);

        // Upgrade missing titles by opening just the visible archives.
        for (var i = 0; i < pageIssues.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            var issue = pageIssues[i];
            if (!string.IsNullOrWhiteSpace(issue.Title) && !string.IsNullOrWhiteSpace(issue.Issue))
            {
                continue;
            }
            if (upgradeBudget <= 0)
            {
                break;
            }
            upgradeBudget--;

            try
            {
                var meta = await _processor.GetSeriesMetadataAsync(issue.FilePath, cancellationToken);
                if (meta is null) continue;
                pageIssues[i] = new SeriesIssueDto
                {
                    FilePath = issue.FilePath,
                    FileName = issue.FileName,
                    Title = string.IsNullOrWhiteSpace(issue.Title) ? meta.Title : issue.Title,
                    Issue = string.IsNullOrWhiteSpace(issue.Issue) ? meta.Issue : issue.Issue,
                    Volume = string.IsNullOrWhiteSpace(issue.Volume) ? meta.Volume : issue.Volume,
                    Publisher = string.IsNullOrWhiteSpace(issue.Publisher) ? meta.Publisher : issue.Publisher,
                    Year = issue.Year ?? meta.Year,
                    Size = issue.Size,
                    Modified = issue.Modified,
                    Processed = issue.Processed,
                    Renamed = issue.Renamed,
                    Normalized = issue.Normalized,
                    Duplicate = issue.Duplicate,
                    Read = issue.Read
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Issue metadata upgrade failed for {FilePath}",
                    LoggingHelper.SanitizePathForLog(issue.FilePath));
            }
        }

        // Final, cheap, unbounded pass: any issue still missing its number
        // (e.g. files beyond the archive-upgrade budget, or files whose archive
        // has no <Number>) gets its number parsed from the file name in memory.
        // This guarantees the issue badge, ordering, and missing-issue grid are
        // populated for every file in the series, not just the first
        // SeriesIssuesMaxArchiveUpgrades of them.
        for (var i = 0; i < pageIssues.Count; i++)
        {
            var issue = pageIssues[i];
            if (!string.IsNullOrWhiteSpace(issue.Issue))
            {
                continue;
            }
            var parsedIssue = ResolveIssueNumber(issue);
            if (string.IsNullOrWhiteSpace(parsedIssue))
            {
                continue;
            }
            pageIssues[i] = new SeriesIssueDto
            {
                FilePath = issue.FilePath,
                FileName = issue.FileName,
                Title = issue.Title,
                Issue = parsedIssue,
                Volume = issue.Volume,
                Publisher = issue.Publisher,
                Year = issue.Year,
                Size = issue.Size,
                Modified = issue.Modified,
                Processed = issue.Processed,
                Renamed = issue.Renamed,
                Normalized = issue.Normalized,
                Duplicate = issue.Duplicate,
                Read = issue.Read
            };
        }

        return new SeriesIssuesResult
        {
            Id = accumulator.Id,
            Title = accumulator.DisplayTitle,
            CanonicalTitle = accumulator.CanonicalTitle,
            Aliases = NormalizeAliases(accumulator.Aliases),
            MetadataSource = accumulator.MetadataSource,
            CoverFilePath = sortedIssues.FirstOrDefault()?.FilePath ?? string.Empty,
            HasExternalImage = accumulator.HasExternalImage,
            ExternalImageUrl = BuildExternalImageUrl(accumulator),
            IssueCount = totalIssues,
            TotalSize = accumulator.TotalSize,
            Issues = pageIssues,
            Page = page,
            PerPage = effectivePerPage,
            TotalPages = totalPages
        };
    }

    private static List<SeriesIssueDto> SortIssues(IEnumerable<SeriesIssueDto> issues)
    {
        return issues
            .OrderBy(issue => ExtractIssueSortKey(ResolveIssueNumber(issue)), new NaturalStringComparer())
            .ThenBy(issue => issue.FileName, new NaturalStringComparer())
            .ToList();
    }

    private static string ExtractIssueSortKey(string? issue)
        => string.IsNullOrWhiteSpace(issue) ? "~" : issue;

    /// <summary>
    /// Returns the issue number for a DTO, falling back to a cheap, in-memory
    /// parse of the file name when the cached metadata lacks one. This keeps
    /// the issue badge, ordering, and missing-issue grid populated for every
    /// file in a series — including series with more than
    /// <see cref="AppSettings.SeriesIssuesMaxArchiveUpgrades"/> files, where the
    /// best-effort archive-upgrade loop would otherwise stop reading titles
    /// from disk and leave later issues' overlays blank.
    /// </summary>
    private static string? ResolveIssueNumber(SeriesIssueDto issue)
    {
        if (!string.IsNullOrWhiteSpace(issue.Issue))
        {
            return issue.Issue;
        }

        return string.IsNullOrWhiteSpace(issue.FileName)
            ? null
            : ComicFileProcessor.ParseChapterNumber(issue.FileName);
    }
}
