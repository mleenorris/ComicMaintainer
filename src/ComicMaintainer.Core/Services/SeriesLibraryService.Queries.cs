using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

public partial class SeriesLibraryService
{
    public async Task<SeriesLibraryResult> GetSeriesAsync(
        string? filter = null,
        string? search = null,
        int page = 1,
        int perPage = 100,
        string? sort = "name",
        string? direction = "asc",
        CancellationToken cancellationToken = default)
    {
        var (fileFilter, seriesFilter) = SplitFilter(filter);
        var groups = await BuildGroupsAsync(fileFilter, allowDiskRead: true, cancellationToken);

        var groupedSeries = groups.Values
            .Where(accumulator => MatchesSeriesFilter(accumulator, seriesFilter))
            .Select(accumulator =>
            {
                // Copy rather than re-assigning accumulator.Issues: accumulators
                // may be shared with other requests via the library snapshot.
                var sortedIssues = new List<SeriesIssueDto>(accumulator.GetSortedIssues());

                return new SeriesLibraryDto
                {
                    Id = accumulator.Id,
                    Title = accumulator.DisplayTitle,
                    CanonicalTitle = accumulator.CanonicalTitle,
                    Aliases = NormalizeAliases(accumulator.Aliases),
                    MetadataSource = accumulator.MetadataSource,
                    IssueCount = sortedIssues.Count,
                    TotalSize = accumulator.TotalSize,
                    LatestModified = ToUnixTime(accumulator.LatestModified),
                    CoverFilePath = sortedIssues.FirstOrDefault()?.FilePath ?? string.Empty,
                    HasExternalImage = accumulator.HasExternalImage,
                    ExternalImageUrl = BuildExternalImageUrl(accumulator),
                    Issues = sortedIssues,
                    Synopsis = accumulator.Synopsis
                };
            })
            .ToList();

        if (!string.IsNullOrWhiteSpace(search))
        {
            groupedSeries = groupedSeries.Where(series =>
                    Contains(series.Title, search)
                    || Contains(series.CanonicalTitle, search)
                    || series.Aliases.Any(alias => Contains(alias, search))
                    || series.Issues.Any(issue =>
                        Contains(issue.Title, search)
                        || Contains(issue.Issue, search)
                        || Contains(issue.FileName, search)))
                .ToList();
        }

        groupedSeries = SortSeries(groupedSeries, sort, direction,
            keyTitle: s => s.Title,
            keyDate: s => s.LatestModified,
            keySize: s => s.TotalSize);

        var totalSeries = groupedSeries.Count;
        var totalPages = perPage == -1 ? 1 : (int)Math.Ceiling((double)totalSeries / Math.Max(1, perPage));
        page = Math.Max(1, Math.Min(page, totalPages == 0 ? 1 : totalPages));

        if (perPage != -1)
        {
            groupedSeries = groupedSeries
                .Skip((page - 1) * perPage)
                .Take(perPage)
                .ToList();
        }

        return new SeriesLibraryResult
        {
            Series = groupedSeries,
            Page = page,
            TotalPages = totalPages,
            TotalSeries = totalSeries
        };
    }

    public async Task<SeriesSummaryResult> GetSeriesSummariesAsync(
        string? filter = null,
        string? search = null,
        int page = 1,
        int perPage = 100,
        string? sort = "name",
        string? direction = "asc",
        int? offset = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var (fileFilter, seriesFilter) = SplitFilter(filter);
        // Summary mode never opens an archive on disk, so even huge libraries
        // stay snappy. Per-issue details are loaded lazily by GetSeriesIssuesAsync.
        var snapshot = await GetGroupsSnapshotAsync(fileFilter, allowDiskRead: false, cancellationToken);

        // The filtered + sorted list depends only on the snapshot and these
        // inputs, so infinite-scroll requests for later pages reuse it and
        // only pay for the slice.
        var viewKey = string.Join("\u001f",
            seriesFilter ?? string.Empty,
            search ?? string.Empty,
            (sort ?? string.Empty).ToLowerInvariant(),
            (direction ?? string.Empty).ToLowerInvariant());
        if (!snapshot.SummaryViews.TryGetValue(viewKey, out var view))
        {
            view = BuildSummaryView(snapshot.Groups.Values, seriesFilter, search, sort, direction);
            if (snapshot.SummaryViews.Count >= MaxSummaryViewsPerSnapshot)
            {
                snapshot.SummaryViews.Clear();
            }
            snapshot.SummaryViews[viewKey] = view;
        }

        IEnumerable<SeriesAccumulator> summaries = view;
        var totalSeries = view.Count;

        bool offsetMode = offset.HasValue && limit.HasValue && limit.Value > 0;
        int effectiveOffset = 0;
        int totalPages;

        if (offsetMode)
        {
            effectiveOffset = Math.Max(0, offset!.Value);
            var lim = Math.Max(1, limit!.Value);
            summaries = view.Skip(effectiveOffset).Take(lim);
            totalPages = (int)Math.Ceiling((double)totalSeries / lim);
            page = 1;
        }
        else
        {
            totalPages = perPage == -1 ? 1 : (int)Math.Ceiling((double)totalSeries / Math.Max(1, perPage));
            page = Math.Max(1, Math.Min(page, totalPages == 0 ? 1 : totalPages));

            if (perPage != -1)
            {
                summaries = view
                    .Skip((page - 1) * perPage)
                    .Take(perPage);
            }
        }

        return new SeriesSummaryResult
        {
            Series = summaries.Select(accumulator => new SeriesSummaryDto
            {
                Id = accumulator.Id,
                Title = accumulator.DisplayTitle,
                CanonicalTitle = accumulator.CanonicalTitle,
                Aliases = NormalizeAliases(accumulator.Aliases),
                MetadataSource = accumulator.MetadataSource,
                IssueCount = accumulator.Issues.Count,
                TotalSize = accumulator.TotalSize,
                LatestModified = ToUnixTime(accumulator.LatestModified),
                CoverFilePath = accumulator.GetSortedIssues().FirstOrDefault()?.FilePath ?? string.Empty,
                HasExternalImage = accumulator.HasExternalImage,
                ExternalImageUrl = BuildExternalImageUrl(accumulator),
                LookupStatus = accumulator.LookupStatus,
                LastLookupUtc = accumulator.LastLookupUtc,
                Synopsis = accumulator.Synopsis
            }).ToList(),
            Page = page,
            TotalPages = totalPages,
            TotalSeries = totalSeries,
            Offset = effectiveOffset
        };
    }

    /// <summary>
    /// Applies the series-level filter, search and sort to a set of grouped
    /// series. The result is shared through the snapshot's view cache, so it
    /// is returned as a read-only list.
    /// </summary>
    private static IReadOnlyList<SeriesAccumulator> BuildSummaryView(
        IEnumerable<SeriesAccumulator> groups,
        string? seriesFilter,
        string? search,
        string? sort,
        string? direction)
    {
        var filtered = groups.Where(accumulator => MatchesSeriesFilter(accumulator, seriesFilter));

        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(accumulator =>
                Contains(accumulator.DisplayTitle, search)
                || Contains(accumulator.CanonicalTitle, search)
                || accumulator.Aliases.Any(alias => Contains(alias, search))
                || Contains(accumulator.IssueSearchText, search));
        }

        return SortSeries(filtered.ToList(), sort, direction,
            keyTitle: accumulator => accumulator.DisplayTitle,
            keyDate: accumulator => ToUnixTime(accumulator.LatestModified),
            keySize: accumulator => accumulator.TotalSize);
    }

    public async Task<IReadOnlyList<SeriesOverviewEntry>> GetSeriesOverviewEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        // Overview never reads archives on disk; it relies on the cached
        // grouping so the home page stays fast even for huge libraries.
        var groups = await BuildGroupsAsync(filter: null, allowDiskRead: false, cancellationToken);

        var entries = new List<SeriesOverviewEntry>(groups.Count);
        foreach (var accumulator in groups.Values)
        {
            var sortedIssues = accumulator.GetSortedIssues();
            var cover = sortedIssues.FirstOrDefault()?.FilePath ?? string.Empty;

            entries.Add(new SeriesOverviewEntry
            {
                Summary = new SeriesSummaryDto
                {
                    Id = accumulator.Id,
                    Title = accumulator.DisplayTitle,
                    CanonicalTitle = accumulator.CanonicalTitle,
                    Aliases = NormalizeAliases(accumulator.Aliases),
                    MetadataSource = accumulator.MetadataSource,
                    IssueCount = accumulator.Issues.Count,
                    TotalSize = accumulator.TotalSize,
                    LatestModified = ToUnixTime(accumulator.LatestModified),
                    CoverFilePath = cover,
                    HasExternalImage = accumulator.HasExternalImage,
                    ExternalImageUrl = BuildExternalImageUrl(accumulator),
                    LookupStatus = accumulator.LookupStatus,
                    LastLookupUtc = accumulator.LastLookupUtc,
                    Synopsis = accumulator.Synopsis
                },
                EarliestCreatedAt = accumulator.EarliestCreatedAt == DateTime.MaxValue
                    ? DateTime.MinValue
                    : accumulator.EarliestCreatedAt,
                LatestCreatedAt = accumulator.LatestCreatedAt,
                // Emit file paths in series (issue) order so the overview can
                // resolve the "next unread issue" when resuming a series.
                FilePaths = sortedIssues.Select(i => i.FilePath).ToList(),
                // Issues marked read via the file read flag (independent of the
                // per-user reading progress) so Continue Reading can recognise a
                // fully-read series and drop it from the row.
                ReadFilePaths = sortedIssues.Where(i => i.Read).Select(i => i.FilePath).ToList()
            });
        }

        return entries;
    }

    /// <summary>
    /// Splits the inbound filter token into a file-level filter (passed to the
    /// file store) and an optional series-level provider-match filter
    /// (<c>matched</c> or <c>unmatched</c>) applied after grouping.
    /// </summary>
    private static (string? FileFilter, string? SeriesFilter) SplitFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return (null, null);
        }

        if (string.Equals(filter, ProviderMatchedFilter, StringComparison.OrdinalIgnoreCase)
            || string.Equals(filter, ProviderUnmatchedFilter, StringComparison.OrdinalIgnoreCase)
            || string.Equals(filter, MissingIssuesFilter, StringComparison.OrdinalIgnoreCase))
        {
            return (null, filter.ToLowerInvariant());
        }

        return (filter, null);
    }

    /// <summary>
    /// Returns true when the accumulator passes the requested series-level
    /// filter. Series-level filters operate on the grouped series record rather
    /// than on individual files: "matched"/"unmatched" key off provider-match
    /// state, while "missing" keys off gaps in the issue numbering. Unknown or
    /// empty filter values disable the filter.
    /// </summary>
    private static bool MatchesSeriesFilter(SeriesAccumulator accumulator, string? seriesFilter)
    {
        if (string.IsNullOrWhiteSpace(seriesFilter))
        {
            return true;
        }

        if (string.Equals(seriesFilter, MissingIssuesFilter, StringComparison.OrdinalIgnoreCase))
        {
            return accumulator.HasMissingIssues(HasMissingIssues);
        }

        // A series counts as "matched" when EITHER it has a recorded metadata
        // source (a successful provider lookup) OR the user has explicitly
        // matched it (lookup_status of "manual_match" / "manual"). Without the
        // status check, manual matches that don't persist a source value end
        // up filed under the "unmatched" filter and rendered with the yellow
        // "?" badge, which is incorrect.
        var hasMatch = !string.IsNullOrWhiteSpace(accumulator.MetadataSource)
            || string.Equals(accumulator.LookupStatus, "manual_match", StringComparison.OrdinalIgnoreCase)
            || string.Equals(accumulator.LookupStatus, "manual", StringComparison.OrdinalIgnoreCase);
        return seriesFilter switch
        {
            ProviderMatchedFilter => hasMatch,
            ProviderUnmatchedFilter => !hasMatch,
            _ => true
        };
    }

    /// <summary>
    /// Returns true when the series has at least one gap in its whole-number
    /// issue numbering. Gaps are detected from the expected starting issue
    /// (issue 1, or issue 0 when the series starts there) up to the highest
    /// issue, so a series that starts above the expected floor (e.g. it has
    /// issues 51-80 but not 1-50) is reported as missing its leading run, not
    /// just gaps strictly between existing issues (e.g. it has 1, 2 and 4 but
    /// not 3). Decimal "specials"/half-chapters (e.g. 2.5) count as a found
    /// issue for their whole number (2), so they can fill an otherwise-missing
    /// slot. Non-numeric issue labels are ignored.
    /// </summary>
    private static bool HasMissingIssues(SeriesAccumulator accumulator)
    {
        var wholeIssues = new HashSet<long>();

        // Summary mode never opens archives on disk, so issue.Issue is often
        // blank for comics that only encode the chapter number in their file
        // name. Resolve it the same way the badge/ordering/missing-issue grid
        // do (parsing the file name as a fallback); otherwise gap detection
        // would see too few whole numbers and wrongly report no missing
        // issues for the entire library.
        foreach (var issueNumber in accumulator.Issues.Select(ResolveIssueNumber))
        {
            if (string.IsNullOrWhiteSpace(issueNumber))
            {
                continue;
            }

            if (!double.TryParse(
                    issueNumber.Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var value))
            {
                continue;
            }

            // A decimal special/half-chapter counts as a found issue for its
            // whole number, so floor the value before recording it.
            wholeIssues.Add((long)Math.Floor(value));
        }

        if (wholeIssues.Count == 0)
        {
            return false;
        }

        var min = wholeIssues.Min();
        var max = wholeIssues.Max();

        // Comics are conventionally numbered from issue 1 (some start at 0), so a
        // series whose lowest issue is above the expected floor is missing the
        // leading run (e.g. it has 51-80 but not 1-50). Start gap detection at the
        // expected floor rather than the series' own minimum so those leading gaps
        // are reported, not just gaps strictly between existing issues.
        var start = Math.Min(min, 1);
        for (var i = start; i <= max; i++)
        {
            if (!wholeIssues.Contains(i))
            {
                return true;
            }
        }

        return false;
    }

    private static SeriesAccumulator? FindMatchingFilteredGroup(
        Dictionary<string, SeriesAccumulator> filteredGroups,
        SeriesAccumulator reference)
    {
        if (filteredGroups.Count == 0) return null;

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                keys.Add(value.Trim());
            }
        }
        Add(reference.CanonicalTitle);
        Add(reference.DisplayTitle);
        foreach (var alias in reference.Aliases) Add(alias);
        if (keys.Count == 0) return null;

        foreach (var candidate in filteredGroups.Values)
        {
            if (!string.IsNullOrWhiteSpace(candidate.CanonicalTitle) && keys.Contains(candidate.CanonicalTitle))
            {
                return candidate;
            }
            if (!string.IsNullOrWhiteSpace(candidate.DisplayTitle) && keys.Contains(candidate.DisplayTitle))
            {
                return candidate;
            }
            foreach (var alias in candidate.Aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias) && keys.Contains(alias))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
