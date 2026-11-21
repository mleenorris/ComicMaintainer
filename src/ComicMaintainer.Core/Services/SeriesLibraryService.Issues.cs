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
        var snapshot = await GetGroupsSnapshotAsync(filter: null, cancellationToken);
        var groupingKeyByFile = snapshot.GroupingKeyByFile;

        if (!snapshot.OwnerByFilePath.TryGetValue(filePath, out var owningSeries))
        {
            return new AdjacentIssueResult { Found = false };
        }

        var sortedIssues = owningSeries.GetSortedIssues().ToList();

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
        var groups = await BuildGroupsAsync(fileFilter, cancellationToken);
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
            //
            // An id no group has at all may be a former one, from before processing renamed the
            // series' files (see TryResolveFormerId); only then is it looked up by name.
            var unfiltered = string.IsNullOrWhiteSpace(fileFilter);
            var unfilteredGroups = unfiltered ? groups : await BuildGroupsAsync(filter: null, cancellationToken);
            if (!TryResolveGroup(unfilteredGroups, seriesId, out var unfilteredAccumulator)
                && !TryResolveFormerId(unfilteredGroups, seriesId, out unfilteredAccumulator))
            {
                return null;
            }

            accumulator = unfiltered ? unfilteredAccumulator : FindMatchingFilteredGroup(groups, unfilteredAccumulator)
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
                    ImageFileName = unfilteredAccumulator.ImageFileName,
                    Synopsis = unfilteredAccumulator.Synopsis,
                    Genres = unfilteredAccumulator.Genres,
                    PublicationStatus = unfilteredAccumulator.PublicationStatus,
                    SiteUrl = unfilteredAccumulator.SiteUrl
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

        var groups = await BuildGroupsAsync(filter: null, cancellationToken);
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
        // Built from the cached archive metadata only; no archive is opened here.
        // Copy: the page list below may be patched in place, and the sorted
        // list can be shared with other requests through the library snapshot.
        var sortedIssues = new List<SeriesIssueDto>(accumulator.GetSortedIssues());
        var totalIssues = sortedIssues.Count;
        var effectivePerPage = perPage == -1 ? totalIssues : Math.Max(1, perPage);
        var totalPages = perPage == -1 ? 1 : (int)Math.Ceiling((double)totalIssues / effectivePerPage);
        page = Math.Max(1, Math.Min(page, totalPages == 0 ? 1 : totalPages));
        var pageIssues = perPage == -1
            ? sortedIssues
            : sortedIssues.Skip((page - 1) * effectivePerPage).Take(effectivePerPage).ToList();

        // Any issue still missing its number (the background scan has not read its
        // archive yet, or the archive has no <Number>) gets it parsed from the file
        // name in memory, so the issue badge, ordering and missing-issue grid are
        // populated for every file in the series.
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
                Read = issue.Read,
                Format = issue.Format,
                VolumeYear = issue.VolumeYear,
                LinkedVolume = issue.LinkedVolume,
                LinkedVolumeYear = issue.LinkedVolumeYear
            };
        }

        var readPaths = await GetReadPathsForProjectionAsync(cancellationToken);
        for (var i = 0; i < pageIssues.Count; i++)
        {
            pageIssues[i] = ProjectIssue(pageIssues[i], readPaths);
        }

        return new SeriesIssuesResult
        {
            Id = accumulator.Id,
            Title = accumulator.DisplayTitle,
            CanonicalTitle = accumulator.CanonicalTitle,
            Aliases = NormalizeAliases(accumulator.Aliases),
            MetadataSource = accumulator.MetadataSource,
            CoverFilePath = sortedIssues.FirstOrDefault()?.FilePath ?? string.Empty,
            CoverVersion = SeriesIssueDto.CoverVersionOf(sortedIssues.FirstOrDefault()),
            HasExternalImage = accumulator.HasExternalImage,
            ExternalImageUrl = BuildExternalImageUrl(accumulator),
            IssueCount = totalIssues,
            TotalSize = accumulator.TotalSize,
            Issues = pageIssues,
            Page = page,
            PerPage = effectivePerPage,
            TotalPages = totalPages,
            Synopsis = accumulator.Synopsis,
            Genres = accumulator.Genres,
            PublicationStatus = accumulator.PublicationStatus,
            SiteUrl = accumulator.SiteUrl
        };
    }

    /// <summary>
    /// Orders a series' issues by issue number. A series split into runs (see
    /// <see cref="VolumeNumbering"/>) lists each run in turn: each volume of a numbering that
    /// restarts by volume, oldest first, then the Annuals, instead of #1 of every volume and
    /// Annual #1 together.
    /// </summary>
    private static List<SeriesIssueDto> SortIssues(IEnumerable<SeriesIssueDto> issues)
    {
        var list = issues as IReadOnlyCollection<SeriesIssueDto> ?? issues.ToList();
        var volumes = RestartingVolumes(list);
        return volumes is null
            ? SortByIssueNumber(list)
            : volumes.SelectMany(volume => SortByIssueNumber(volume.Items)).ToList();
    }

    private static List<SeriesIssueDto> SortByIssueNumber(IEnumerable<SeriesIssueDto> issues)
    {
        return issues
            .OrderBy(issue => ExtractIssueSortKey(ResolveIssueNumber(issue)), new NaturalStringComparer())
            .ThenBy(issue => issue.FileName, new NaturalStringComparer())
            .ToList();
    }

    /// <summary>
    /// The series' issues split into runs numbered on their own (Annuals, and volumes of a
    /// numbering that restarts by volume); null when it is one run. An issue's number falls
    /// back to its file name, as for ordering.
    /// </summary>
    internal static IReadOnlyList<VolumeNumbering.VolumeGroup<SeriesIssueDto>>? RestartingVolumes(
        IEnumerable<SeriesIssueDto> issues)
        => VolumeNumbering.SeparateRuns(issues, issue => issue.VolumeInfo, ResolveIssueNumber, issue => issue.Format);

    /// <summary>
    /// Links the volume numbers and start years the issues of one series name. The volume number
    /// is in ComicInfo and the start year in the database, and not every issue has both: when
    /// one issue names volume 2 with start year 2016, an issue that names only 2016 is listed as
    /// volume 2, and one that names only volume 2 gets 2016. A year or number linked to more than
    /// one counterpart is left alone.
    /// </summary>
    internal static void LinkVolumeNumbersAndYears(IEnumerable<SeriesIssueDto> issues)
    {
        var list = issues as IReadOnlyCollection<SeriesIssueDto> ?? issues.ToList();
        var numbersByYear = new Dictionary<int, HashSet<int>>();
        var yearsByNumber = new Dictionary<int, HashSet<int>>();
        foreach (var issue in list)
        {
            issue.LinkedVolume = null;
            issue.LinkedVolumeYear = null;
            if (issue.OwnVolume is { IsYear: false } number && issue.VolumeYear is { } year)
            {
                Add(numbersByYear, year, number.Value);
                Add(yearsByNumber, number.Value, year);
            }
        }

        if (numbersByYear.Count == 0)
        {
            return;
        }

        foreach (var issue in list)
        {
            switch (issue.OwnVolume)
            {
                case { IsYear: true } own when numbersByYear.TryGetValue(own.Value, out var numbers) && numbers.Count == 1:
                    issue.LinkedVolume = new ComicVolume(numbers.Single(), IsYear: false);
                    issue.LinkedVolumeYear = own.Value;
                    break;
                case { IsYear: false } own when issue.VolumeYear is null
                                                && yearsByNumber.TryGetValue(own.Value, out var years) && years.Count == 1:
                    issue.LinkedVolumeYear = years.Single();
                    break;
            }
        }

        static void Add(Dictionary<int, HashSet<int>> map, int key, int value)
        {
            if (!map.TryGetValue(key, out var set))
            {
                map[key] = set = [];
            }

            set.Add(value);
        }
    }

    private static string ExtractIssueSortKey(string? issue)
        => string.IsNullOrWhiteSpace(issue) ? "~" : issue;

    /// <summary>
    /// Returns the issue number for a DTO, falling back to a cheap, in-memory
    /// parse of the file name when the cached metadata lacks one. This keeps
    /// the issue badge, ordering, and missing-issue grid populated for every
    /// file in a series, including files whose archive the background
    /// archive-metadata scan has not read yet.
    /// </summary>
    private static string? ResolveIssueNumber(SeriesIssueDto issue)
    {
        if (!string.IsNullOrWhiteSpace(issue.Issue))
        {
            return issue.Issue;
        }

        if (string.IsNullOrWhiteSpace(issue.FileName))
        {
            return null;
        }

        // An Annual named by its year ("Annual 2026") has no issue number: the year is not one,
        // and would leave its run "missing" every number before it.
        var parsed = IssueNumberParser.Parse(Path.GetFileNameWithoutExtension(issue.FileName));
        return issue.Annual && IssueNumberParser.IsYear(parsed) ? null : parsed;
    }
}
