using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Talks to a Suwayomi server over its GraphQL endpoint
/// (<c>{BaseUrl}/api/graphql</c>) so the service can queue downloads for the
/// issues a locally-tracked series is missing, and find series on the
/// server's sources and add them to its library.
/// </summary>
/// <remarks>
/// Missing-issue downloads prefer matching entries already in the Suwayomi
/// library. If those entries do not contain every requested issue, installed
/// sources are searched too. Source-search results can have their chapters
/// refreshed and downloaded without marking the series as followed; adding a
/// series to the library remains an explicit user action
/// (<see cref="AddToLibraryAsync"/>).
/// </remarks>
public class SuwayomiService : ISuwayomiService
{
    public const string HttpClientName = "Suwayomi";

    private const int MaxLibraryPageSize = 500;
    private const int SourceSearchConcurrency = 4;
    private const string LocalSourceId = "0";
    private const int MaxThumbnailBytes = 10 * 1024 * 1024;
    private const string UnexpectedResponseMessage = "The Suwayomi server returned an unexpected response.";
    private const string UnexpectedChapterListResponseMessage =
        "The Suwayomi server returned an unexpected response: data.chapters.nodes must be an array.";

    // Searching a source and refreshing a chapter list make Suwayomi query the
    // source's site, which can be far slower than the client's usual timeout.
    private static readonly TimeSpan SourceSearchTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ChapterFetchTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ThumbnailTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<SuwayomiService> _logger;

    public SuwayomiService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppSettings> settings,
        ILogger<SuwayomiService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
    }

    public bool IsConfigured
    {
        get
        {
            var config = _settings.CurrentValue;
            return config.EnableSuwayomi && TryBuildEndpoint(config.SuwayomiBaseUrl, out _);
        }
    }

    public async Task<SuwayomiConnectionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var config = _settings.CurrentValue;
        var status = new SuwayomiConnectionStatus
        {
            Enabled = config.EnableSuwayomi,
            Configured = TryBuildEndpoint(config.SuwayomiBaseUrl, out _)
        };

        if (!status.Enabled)
        {
            status.Error = "The Suwayomi integration is disabled.";
            return status;
        }

        if (!status.Configured)
        {
            status.Error = "No valid Suwayomi server URL is configured.";
            return status;
        }

        try
        {
            using var document = await ExecuteAsync(
                "AboutServer",
                "query { aboutServer { name version } }",
                null,
                cancellationToken);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("aboutServer", out var about)
                || about.ValueKind != JsonValueKind.Object)
            {
                throw new SuwayomiException(UnexpectedResponseMessage);
            }

            status.Connected = true;
            status.ServerName = GetStringOrNull(about, "name");
            status.Version = GetStringOrNull(about, "version");
        }
        catch (SuwayomiException ex)
        {
            status.Error = ex.Message;
        }

        return status;
    }

    public async Task<IReadOnlyList<SuwayomiSeries>> SearchSeriesAsync(
        string? query,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var library = (await GetLibrarySeriesAsync(cancellationToken)).Series;

        if (string.IsNullOrWhiteSpace(query))
        {
            return library.Take(limit).ToList();
        }

        var normalizedQuery = Normalize(query);
        if (normalizedQuery.Length == 0)
        {
            return library.Take(limit).ToList();
        }

        return RankMatches(library, normalizedQuery)
            .Take(limit)
            .ToList();
    }

    public async Task<SuwayomiLibrarySnapshot> GetLibrarySeriesAsync(
        CancellationToken cancellationToken = default)
    {
        var series = await GetLibrarySeriesInternalAsync(cancellationToken);
        var normalizedSeries = series
            .Select(item => (Series: item, NormalizedTitle: Normalize(item.Title)))
            .ToList();
        var titleIndex = new Dictionary<string, SuwayomiSeries>(StringComparer.Ordinal);
        foreach (var item in normalizedSeries
                     .Where(item => item.NormalizedTitle.Length > 0)
                     .OrderBy(item => item.Series.Title, StringComparer.OrdinalIgnoreCase))
        {
            titleIndex.TryAdd(item.NormalizedTitle, item.Series);
        }

        return new SuwayomiLibrarySnapshot
        {
            Series = series,
            NormalizedTitleIndex = titleIndex,
            NormalizedSeries = normalizedSeries
        };
    }

    public async Task<IReadOnlyList<SuwayomiChapter>> GetChaptersAsync(
        int seriesId,
        CancellationToken cancellationToken = default)
    {
        const string queryText = """
            query Chapters($id: Int!) {
              chapters(condition: { mangaId: $id }) {
                nodes { id name chapterNumber isDownloaded }
              }
            }
            """;

        using var document = await ExecuteAsync(
            "Chapters",
            queryText,
            new Dictionary<string, object?> { ["id"] = seriesId },
            cancellationToken);

        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("chapters", out var connection)
            || connection.ValueKind != JsonValueKind.Object
            || !connection.TryGetProperty("nodes", out var nodes)
            || nodes.ValueKind != JsonValueKind.Array)
        {
            throw new SuwayomiException(UnexpectedChapterListResponseMessage);
        }

        return ReadChapters(nodes)
            .OrderBy(c => c.ChapterNumber)
            .ToList();
    }

    private static List<SuwayomiChapter> ReadChapters(JsonElement nodes)
    {
        var chapters = new List<SuwayomiChapter>();
        foreach (var node in nodes.EnumerateArray())
        {
            var id = GetInt32OrNull(node, "id");
            if (id is null)
            {
                continue;
            }

            chapters.Add(new SuwayomiChapter
            {
                Id = id.Value,
                Name = GetStringOrNull(node, "name") ?? $"Chapter {id.Value}",
                ChapterNumber = GetDoubleOrNull(node, "chapterNumber") ?? -1,
                IsDownloaded = GetBooleanOrNull(node, "isDownloaded") ?? false
            });
        }

        return chapters;
    }

    public async Task<SuwayomiDownloadResult> DownloadMissingIssuesAsync(
        string? seriesTitle,
        IEnumerable<int> issueNumbers,
        int? seriesId = null,
        IEnumerable<string>? alternateTitles = null,
        CancellationToken cancellationToken = default)
    {
        return await DownloadMissingIssuesCoreAsync(
            seriesTitle, issueNumbers, seriesId, alternateTitles, librarySnapshot: null, cancellationToken);
    }

    public async Task<SuwayomiDownloadResult> DownloadMissingIssuesFromSnapshotAsync(
        string? seriesTitle,
        IEnumerable<int> issueNumbers,
        SuwayomiLibrarySnapshot librarySnapshot,
        IEnumerable<string>? alternateTitles = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(librarySnapshot);
        return await DownloadMissingIssuesCoreAsync(
            seriesTitle, issueNumbers, seriesId: null, alternateTitles, librarySnapshot, cancellationToken);
    }

    private async Task<SuwayomiDownloadResult> DownloadMissingIssuesCoreAsync(
        string? seriesTitle,
        IEnumerable<int> issueNumbers,
        int? seriesId,
        IEnumerable<string>? alternateTitles,
        SuwayomiLibrarySnapshot? librarySnapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issueNumbers);

        var requested = issueNumbers
            .Where(n => n >= 0)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        if (requested.Count == 0)
        {
            throw new SuwayomiException("No missing issue numbers were supplied.");
        }

        var result = new SuwayomiDownloadResult();
        var unresolved = requested.ToHashSet();
        var chapterIds = new HashSet<int>();
        var checkedSeriesIds = new HashSet<int>();
        SuwayomiSeries? firstMatchedSeries = null;

        if (seriesId is > 0)
        {
            var series = await ResolveSeriesAsync(
                seriesTitle, alternateTitles, seriesId, librarySnapshot, cancellationToken);
            checkedSeriesIds.Add(series.Id);
            firstMatchedSeries = series;
            result.Series = series;
            CollectMissingChapters(
                series,
                await GetChaptersAsync(series.Id, cancellationToken),
                unresolved,
                chapterIds,
                result);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(seriesTitle))
            {
                throw new SuwayomiException("A series title or Suwayomi series id is required.");
            }

            var normalizedTitle = Normalize(seriesTitle);
            var searchTitles = BuildSearchTitles(seriesTitle, alternateTitles);
            librarySnapshot ??= await GetLibrarySeriesAsync(cancellationToken);
            var libraryCandidates = FindLibraryCandidates(librarySnapshot, normalizedTitle, searchTitles);
            var hasExactLibraryMatch = libraryCandidates.Any(series =>
                ExactTitlePriority(Normalize(series.Title), searchTitles) >= 0);

            foreach (var series in libraryCandidates)
            {
                if (!checkedSeriesIds.Add(series.Id))
                {
                    continue;
                }

                firstMatchedSeries ??= series;
                if (CollectMissingChapters(
                        series,
                        await GetChaptersAsync(series.Id, cancellationToken),
                        unresolved,
                        chapterIds,
                        result))
                {
                    result.Series ??= series;
                }

                if (unresolved.Count == 0)
                {
                    break;
                }
            }

            if (unresolved.Count > 0 && searchTitles.Count > 0)
            {
                var discovery = await DiscoverSourceCandidatesAsync(
                    normalizedTitle,
                    searchTitles,
                    !hasExactLibraryMatch,
                    librarySnapshot,
                    checkedSeriesIds,
                    unresolved,
                    chapterIds,
                    result,
                    cancellationToken);
                firstMatchedSeries ??= discovery.FirstMatchedSeries;
            }

            if (firstMatchedSeries is null)
            {
                var searched = DescribeSearchedTitles(
                    seriesTitle,
                    searchTitles
                        .Where(title => title.Key != normalizedTitle)
                        .Select(title => title.Title));
                var message = $"No series matching {searched} was found in the Suwayomi library or installed sources.";
                if (result.DiscoveryFailures.Count > 0)
                {
                    var firstFailure = result.DiscoveryFailures[0];
                    message += $" {result.DiscoveryFailures.Count} source check(s) failed; "
                               + $"{firstFailure.Target}: {firstFailure.Error}";
                }

                throw new SuwayomiException(message);
            }
        }

        result.Series ??= firstMatchedSeries;
        result.UnmatchedIssueNumbers.AddRange(unresolved.OrderBy(number => number));
        result.AlreadyDownloadedIssueNumbers.Sort();

        if (chapterIds.Count > 0)
        {
            await EnqueueChapterDownloadsAsync(chapterIds.ToList(), cancellationToken);
            _logger.LogInformation(
                "Queued {Count} Suwayomi chapter download(s) for {SeriesTitle} after checking {SeriesCount} matching series",
                chapterIds.Count,
                LoggingHelper.SanitizeForLog(seriesTitle ?? result.Series?.Title ?? string.Empty),
                checkedSeriesIds.Count);
        }

        return result;
    }

    private static bool CollectMissingChapters(
        SuwayomiSeries series,
        IReadOnlyList<SuwayomiChapter> chapters,
        HashSet<int> unresolved,
        HashSet<int> chapterIds,
        SuwayomiDownloadResult result)
    {
        var matchedAny = false;
        foreach (var issueNumber in unresolved.OrderBy(number => number).ToList())
        {
            // A chapter covers an issue number when its (floored) chapter
            // number matches, which mirrors how the library treats decimal
            // "half chapters" as belonging to their whole number.
            var candidates = chapters
                .Where(c => c.ChapterNumber >= 0 && (int)Math.Floor(c.ChapterNumber) == issueNumber)
                .ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            matchedAny = true;
            result.Series ??= series;
            var pending = candidates.Where(c => !c.IsDownloaded).ToList();
            if (pending.Count == 0)
            {
                result.AlreadyDownloadedIssueNumbers.Add(issueNumber);
            }
            else
            {
                foreach (var chapter in pending)
                {
                    if (chapterIds.Add(chapter.Id))
                    {
                        result.QueuedChapters.Add(chapter);
                    }
                }
            }

            unresolved.Remove(issueNumber);
        }

        return matchedAny;
    }

    private async Task<SourceDiscoveryResult> DiscoverSourceCandidatesAsync(
        string normalizedTitle,
        IReadOnlyList<(string Title, string Key)> searchTitles,
        bool allowPartialMatches,
        SuwayomiLibrarySnapshot librarySnapshot,
        HashSet<int> checkedSeriesIds,
        HashSet<int> unresolved,
        HashSet<int> chapterIds,
        SuwayomiDownloadResult result,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SuwayomiSource> sources;
        try
        {
            sources = librarySnapshot.Sources ??= await GetSourcesAsync(cancellationToken);
        }
        catch (SuwayomiException ex)
        {
            AddDiscoveryFailure(result, "Installed Suwayomi sources", ex);
            return new SourceDiscoveryResult(null);
        }

        if (sources.Count == 0)
        {
            return new SourceDiscoveryResult(null);
        }

        var outcomes = new SourceSearchOutcome[sources.Count];
        for (var offset = 0; offset < sources.Count; offset += SourceSearchConcurrency)
        {
            var count = Math.Min(SourceSearchConcurrency, sources.Count - offset);
            await Task.WhenAll(Enumerable.Range(offset, count).Select(async index =>
            {
                outcomes[index] = await SearchSourceForCandidatesAsync(
                    sources[index],
                    index,
                    normalizedTitle,
                    searchTitles,
                    cancellationToken);
            }));
        }

        foreach (var outcome in outcomes)
        {
            if (outcome.Error is not null)
            {
                AddDiscoveryFailure(result, outcome.Source.DisplayName, outcome.Error);
            }
        }

        var found = outcomes
            .SelectMany(outcome => outcome.Candidates)
            .ToList();
        var available = found
            .Where(candidate => !checkedSeriesIds.Contains(candidate.Series.Id))
            .ToList();
        var exact = available.Where(candidate => candidate.ExactTitlePriority >= 0).ToList();
        var exactFound = found.Any(candidate => candidate.ExactTitlePriority >= 0);
        var selected = exactFound || !allowPartialMatches
            ? exact
                .OrderBy(candidate => candidate.ExactTitlePriority)
                .ThenBy(candidate => candidate.SourceIndex)
                .ThenBy(candidate => candidate.ResultIndex)
            : available
                .Where(candidate => candidate.PartialScore > 0)
                .OrderByDescending(candidate => candidate.PartialScore)
                .ThenBy(candidate => candidate.SourceIndex)
                .ThenBy(candidate => candidate.ResultIndex);

        SuwayomiSeries? firstMatchedSeries = null;
        foreach (var candidate in selected.DistinctBy(candidate => candidate.Series.Id))
        {
            if (!checkedSeriesIds.Add(candidate.Series.Id))
            {
                continue;
            }

            firstMatchedSeries ??= candidate.Series;
            IReadOnlyList<SuwayomiChapter> chapters;
            try
            {
                chapters = await FetchChaptersAsync(candidate.Series.Id, cancellationToken);
            }
            catch (SuwayomiException ex)
            {
                AddDiscoveryFailure(
                    result,
                    $"{candidate.Source.DisplayName}: {candidate.Series.Title}",
                    ex);
                continue;
            }

            if (CollectMissingChapters(
                    candidate.Series,
                    chapters,
                    unresolved,
                    chapterIds,
                    result))
            {
                result.Series ??= candidate.Series;
            }

            if (unresolved.Count == 0)
            {
                break;
            }
        }

        return new SourceDiscoveryResult(firstMatchedSeries);
    }

    private async Task<SourceSearchOutcome> SearchSourceForCandidatesAsync(
        SuwayomiSource source,
        int sourceIndex,
        string normalizedTitle,
        IReadOnlyList<(string Title, string Key)> searchTitles,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(source.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var sourceId))
        {
            return new SourceSearchOutcome(source, new List<SourceSeriesCandidate>(), "The source id is invalid.");
        }

        var candidates = new List<SourceSeriesCandidate>();
        var resultIndex = 0;
        for (var queryIndex = 0; queryIndex < searchTitles.Count; queryIndex++)
        {
            SuwayomiSourceSearchResult search;
            try
            {
                search = await SearchSourceAsync(
                    sourceId,
                    searchTitles[queryIndex].Title,
                    cancellationToken: cancellationToken);
            }
            catch (SuwayomiException ex)
            {
                return new SourceSearchOutcome(source, candidates, ex.Message);
            }

            foreach (var series in search.Series)
            {
                series.Source = source.DisplayName;
                var key = Normalize(series.Title);
                var exactTitlePriority = ExactTitlePriority(key, searchTitles);
                var partialScore = queryIndex == 0 ? MatchScore(key, normalizedTitle) : 0;
                if (exactTitlePriority >= 0 || partialScore > 0)
                {
                    candidates.Add(new SourceSeriesCandidate(
                        source,
                        sourceIndex,
                        resultIndex++,
                        series,
                        exactTitlePriority,
                        partialScore));
                }
            }

            // A source that matched any known title does not need the lower
            // priority aliases. Otherwise try each alias in the supplied order.
            if (candidates.Any(candidate => candidate.ExactTitlePriority >= 0))
            {
                break;
            }
        }

        return new SourceSearchOutcome(source, candidates, null);
    }

    private void AddDiscoveryFailure(
        SuwayomiDownloadResult result,
        string target,
        SuwayomiException exception)
        => AddDiscoveryFailure(result, target, exception.Message);

    private void AddDiscoveryFailure(
        SuwayomiDownloadResult result,
        string target,
        string error)
    {
        result.DiscoveryFailures.Add(new SuwayomiDownloadFailure
        {
            Target = target,
            Error = error
        });
        _logger.LogWarning(
            "Suwayomi missing-issue discovery could not check {Target}: {Message}",
            LoggingHelper.SanitizeForLog(target),
            LoggingHelper.SanitizeForLog(error));
    }

    private static List<SuwayomiSeries> FindLibraryCandidates(
        SuwayomiLibrarySnapshot library,
        string normalizedTitle,
        IReadOnlyList<(string Title, string Key)> searchTitles)
    {
        var exact = searchTitles
            .SelectMany(title => library.NormalizedSeries
                .Where(item => item.NormalizedTitle == title.Key)
                .OrderBy(item => item.Series.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Series.Id)
                .Select(item => item.Series))
            .DistinctBy(series => series.Id)
            .ToList();
        if (exact.Count > 0)
        {
            return exact;
        }

        return normalizedTitle.Length == 0
            ? new List<SuwayomiSeries>()
            : RankMatches(library, normalizedTitle).ToList();
    }

    private static List<(string Title, string Key)> BuildSearchTitles(
        string seriesTitle,
        IEnumerable<string>? alternateTitles)
    {
        var titles = new List<(string Title, string Key)>();
        Add(seriesTitle);
        foreach (var title in alternateTitles ?? Enumerable.Empty<string>())
        {
            Add(title);
        }

        return titles;

        void Add(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            var trimmed = title.Trim();
            var key = Normalize(trimmed);
            if (key.Length > 0 && titles.All(existing => existing.Key != key))
            {
                titles.Add((trimmed, key));
            }
        }
    }

    private static int ExactTitlePriority(
        string candidate,
        IReadOnlyList<(string Title, string Key)> searchTitles)
    {
        for (var index = 0; index < searchTitles.Count; index++)
        {
            if (candidate == searchTitles[index].Key)
            {
                return index;
            }
        }

        return -1;
    }

    private static string DescribeSearchedTitles(string seriesTitle, IEnumerable<string> alternateTitles)
    {
        var others = alternateTitles.ToList();
        return others.Count switch
        {
            0 => $"'{seriesTitle}'",
            1 => $"'{seriesTitle}' or its other title '{others[0]}'",
            _ => $"'{seriesTitle}' or any of its {others.Count} other titles"
        };
    }

    private sealed record SourceDiscoveryResult(SuwayomiSeries? FirstMatchedSeries);

    private sealed record SourceSearchOutcome(
        SuwayomiSource Source,
        List<SourceSeriesCandidate> Candidates,
        string? Error);

    private sealed record SourceSeriesCandidate(
        SuwayomiSource Source,
        int SourceIndex,
        int ResultIndex,
        SuwayomiSeries Series,
        int ExactTitlePriority,
        int PartialScore);

    private async Task EnqueueChapterDownloadsAsync(List<int> chapterIds, CancellationToken cancellationToken)
    {
        const string mutation = """
            mutation Enqueue($ids: [Int!]!) {
              enqueueChapterDownloads(input: { ids: $ids }) {
                clientMutationId
              }
            }
            """;

        using var _ = await ExecuteAsync(
            "Enqueue",
            mutation,
            new Dictionary<string, object?> { ["ids"] = chapterIds },
            cancellationToken);
    }

    public async Task<IReadOnlyList<SuwayomiSource>> GetSourcesAsync(CancellationToken cancellationToken = default)
    {
        const string queryText = """
            query Sources {
              sources {
                nodes { id name lang displayName }
              }
            }
            """;

        using var document = await ExecuteAsync("Sources", queryText, null, cancellationToken);

        var data = document.RootElement.GetProperty("data");
        if (!data.TryGetProperty("sources", out var connection)
            || connection.ValueKind != JsonValueKind.Object
            || !connection.TryGetProperty("nodes", out var nodes)
            || nodes.ValueKind != JsonValueKind.Array)
        {
            throw new SuwayomiException(UnexpectedResponseMessage);
        }

        var sources = new List<SuwayomiSource>();
        foreach (var node in nodes.EnumerateArray())
        {
            // Local lists the files in Suwayomi's own local folder, which have
            // nothing to download.
            var id = GetSourceIdOrNull(node, "id");
            if (id is null || id == LocalSourceId)
            {
                continue;
            }

            var name = GetStringOrNull(node, "name");
            var displayName = GetStringOrNull(node, "displayName");
            sources.Add(new SuwayomiSource
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? id : name,
                Lang = GetStringOrNull(node, "lang") ?? string.Empty,
                DisplayName = !string.IsNullOrWhiteSpace(displayName)
                    ? displayName
                    : string.IsNullOrWhiteSpace(name) ? id : name
            });
        }

        return sources
            .DistinctBy(source => source.Id)
            .OrderBy(source => source.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Id, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<SuwayomiSourceSearchResult> SearchSourceAsync(
        long sourceId,
        string query,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        if (sourceId <= 0)
        {
            throw new SuwayomiException("A Suwayomi source id is required.");
        }

        var term = query?.Trim();
        if (string.IsNullOrEmpty(term))
        {
            throw new SuwayomiException("A search term is required.");
        }

        // Source ids are 64-bit, which Suwayomi's LongString scalar carries as
        // a JSON string.
        const string mutation = """
            mutation SearchSource($source: LongString!, $query: String!, $page: Int!) {
              fetchSourceManga(input: { source: $source, type: SEARCH, query: $query, page: $page }) {
                mangas { id title inLibrary thumbnailUrl }
                hasNextPage
              }
            }
            """;

        using var document = await ExecuteAsync(
            "SearchSource",
            mutation,
            new Dictionary<string, object?>
            {
                ["source"] = sourceId.ToString(CultureInfo.InvariantCulture),
                ["query"] = term,
                ["page"] = Math.Max(1, page)
            },
            cancellationToken,
            SourceSearchTimeout);

        var data = document.RootElement.GetProperty("data");
        if (!data.TryGetProperty("fetchSourceManga", out var payload)
            || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("mangas", out var mangas)
            || mangas.ValueKind != JsonValueKind.Array)
        {
            throw new SuwayomiException(UnexpectedResponseMessage);
        }

        var found = mangas.EnumerateArray()
            .Select(ReadSeries)
            .OfType<SuwayomiSeries>()
            .ToList();

        // Sources order their results by their own notion of relevance, which
        // often buries the exact title; OrderByDescending is stable, so the
        // source's order holds within each score.
        var normalizedTerm = Normalize(term);
        return new SuwayomiSourceSearchResult
        {
            Series = found
                .DistinctBy(series => series.Id)
                .OrderByDescending(series => MatchScore(Normalize(series.Title), normalizedTerm))
                .ToList(),
            HasNextPage = GetBooleanOrNull(payload, "hasNextPage") ?? false
        };
    }

    public async Task<SuwayomiAddToLibraryResult> AddToLibraryAsync(
        int seriesId,
        CancellationToken cancellationToken = default)
    {
        var series = await GetMangaAsync(seriesId, cancellationToken);
        if (series.InLibrary)
        {
            // Setting inLibrary again would reset the date Suwayomi lists the
            // series as added on.
            return new SuwayomiAddToLibraryResult { Series = series, AlreadyInLibrary = true };
        }

        const string mutation = """
            mutation AddToLibrary($id: Int!) {
              updateManga(input: { id: $id, patch: { inLibrary: true } }) {
                manga { id title inLibrary thumbnailUrl }
              }
            }
            """;

        using var document = await ExecuteAsync(
            "AddToLibrary",
            mutation,
            new Dictionary<string, object?> { ["id"] = seriesId },
            cancellationToken);

        var data = document.RootElement.GetProperty("data");
        if (!data.TryGetProperty("updateManga", out var payload)
            || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("manga", out var manga)
            || ReadSeries(manga) is not { } added)
        {
            throw new SuwayomiException(UnexpectedResponseMessage);
        }

        if (!added.InLibrary)
        {
            throw new SuwayomiException($"The Suwayomi server did not add '{added.Title}' to its library.");
        }

        _logger.LogInformation(
            "Added series {SeriesId} ({SeriesTitle}) to the Suwayomi library",
            added.Id,
            LoggingHelper.SanitizeForLog(added.Title));

        return new SuwayomiAddToLibraryResult { Series = added, AlreadyInLibrary = false };
    }

    public async Task<SuwayomiSeriesDownloadResult> QueueSeriesDownloadsAsync(
        int seriesId,
        IEnumerable<string?>? ownedIssueNumbers = null,
        CancellationToken cancellationToken = default)
    {
        if (seriesId <= 0)
        {
            throw new SuwayomiException($"Suwayomi has no series with id {seriesId}.");
        }

        var owned = (ownedIssueNumbers ?? Enumerable.Empty<string?>())
            .Select(text => TryParseIssueNumber(text, out var number) ? ChapterKey(number) : default(float?))
            .OfType<float>()
            .ToHashSet();

        var chapters = await FetchChaptersAsync(seriesId, cancellationToken);
        var result = new SuwayomiSeriesDownloadResult { ChapterCount = chapters.Count };
        var queue = new List<SuwayomiChapter>();
        var numbered = chapters.Where(c => c.ChapterNumber >= 0).ToList();

        if (numbered.Count == 0)
        {
            // A oneshot, or a source that numbers nothing: every chapter counts.
            foreach (var chapter in chapters)
            {
                if (chapter.IsDownloaded)
                {
                    result.AlreadyDownloadedCount++;
                }
                else
                {
                    queue.Add(chapter);
                }
            }
        }
        else
        {
            // Unnumbered chapters beside numbered ones are extras and notices
            // the library could not place as issues.
            result.UnnumberedChapterCount = chapters.Count - numbered.Count;

            // Chapters sharing a number are usually the same chapter from
            // different scanlators; one of them is enough.
            foreach (var sameNumber in numbered.GroupBy(c => ChapterKey(c.ChapterNumber)).OrderBy(g => g.Key))
            {
                if (owned.Contains(sameNumber.Key))
                {
                    result.AlreadyInLocalLibraryCount++;
                }
                else if (sameNumber.Any(c => c.IsDownloaded))
                {
                    result.AlreadyDownloadedCount++;
                }
                else
                {
                    queue.Add(sameNumber.First());
                    result.DuplicateChapterCount += sameNumber.Count() - 1;
                }
            }
        }

        if (queue.Count == 0)
        {
            return result;
        }

        await EnqueueChapterDownloadsAsync(queue.Select(c => c.Id).ToList(), cancellationToken);
        result.QueuedChapters.AddRange(queue);

        _logger.LogInformation(
            "Queued {Count} Suwayomi chapter download(s) for series {SeriesId}",
            queue.Count,
            seriesId);

        return result;
    }

    public async Task<SuwayomiThumbnail> GetThumbnailAsync(int seriesId, CancellationToken cancellationToken = default)
    {
        if (seriesId <= 0)
        {
            throw new SuwayomiException($"Suwayomi has no series with id {seriesId}.");
        }

        var config = _settings.CurrentValue;
        var endpoint = ResolveEndpoint(config);

        // The REST API sits beside the GraphQL endpoint, so a server published
        // under a path prefix keeps it.
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(endpoint, $"v1/manga/{seriesId.ToString(CultureInfo.InvariantCulture)}/thumbnail"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        ApplyCredentials(request, config);

        using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ThumbnailTimeout);

        byte[] content;
        try
        {
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new SuwayomiException(
                    $"The Suwayomi server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            if (response.Content.Headers.ContentLength > MaxThumbnailBytes)
            {
                throw new SuwayomiException("The Suwayomi cover is too large.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            content = await ReadCappedAsync(stream, MaxThumbnailBytes, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            // Covers come from the source's site, so a source that is down
            // fails every cover of a search; the search itself reports it.
            _logger.LogDebug(ex, "Suwayomi cover request failed");
            throw new SuwayomiException($"Could not reach the Suwayomi server: {ex.Message}", ex);
        }

        // The type is taken from the bytes, not the server's header, so
        // nothing but an image is ever served from this origin.
        var contentType = DetectImageContentType(content)
            ?? throw new SuwayomiException("The Suwayomi server did not return a supported image.");
        return new SuwayomiThumbnail(content, contentType);
    }

    private async Task<List<SuwayomiChapter>> FetchChaptersAsync(int seriesId, CancellationToken cancellationToken)
    {
        // Unlike the chapters query, which only reads what Suwayomi has
        // stored, this refreshes the list from the source: a series that was
        // just found or added may have none stored yet. It answers in the
        // source's order.
        const string mutation = """
            mutation FetchChapters($id: Int!) {
              fetchChapters(input: { mangaId: $id }) {
                chapters { id name chapterNumber isDownloaded }
              }
            }
            """;

        using var document = await ExecuteAsync(
            "FetchChapters",
            mutation,
            new Dictionary<string, object?> { ["id"] = seriesId },
            cancellationToken,
            ChapterFetchTimeout);

        var data = document.RootElement.GetProperty("data");
        if (!data.TryGetProperty("fetchChapters", out var payload)
            || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("chapters", out var nodes)
            || nodes.ValueKind != JsonValueKind.Array)
        {
            throw new SuwayomiException(UnexpectedResponseMessage);
        }

        return ReadChapters(nodes);
    }

    /// <summary>
    /// Parses a local issue number such as "7", "007" or "10.5"; labels such
    /// as "Annual" have no chapter to match.
    /// </summary>
    private static bool TryParseIssueNumber(string? text, out double number)
    {
        number = 0;
        var trimmed = text?.Trim();
        return !string.IsNullOrEmpty(trimmed)
               && trimmed.All(c => char.IsAsciiDigit(c) || c == '.')
               && double.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>
    /// Suwayomi stores chapter numbers as 32-bit floats, so 10.1 arrives as
    /// 10.100000381…; narrowing both sides to that precision lets it equal
    /// the local "10.1" while keeping 10.001 and 10.002 apart.
    /// </summary>
    private static float ChapterKey(double number) => (float)number;

    private static async Task<byte[]> ReadCappedAsync(Stream source, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new SuwayomiException("The Suwayomi cover is too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string? DetectImageContentType(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (data.Length >= 8
            && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
            && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            return "image/png";
        }

        // GIF87a / GIF89a
        if (data.Length >= 6
            && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38
            && (data[4] == 0x37 || data[4] == 0x39) && data[5] == 0x61)
        {
            return "image/gif";
        }

        // RIFF....WEBP
        if (data.Length >= 12
            && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
        {
            return "image/webp";
        }

        return null;
    }

    private async Task<SuwayomiSeries> ResolveSeriesAsync(
        string? seriesTitle,
        IEnumerable<string>? alternateTitles,
        int? seriesId,
        SuwayomiLibrarySnapshot? librarySnapshot,
        CancellationToken cancellationToken)
    {
        if (seriesId is > 0)
        {
            var library = librarySnapshot?.Series
                          ?? (await GetLibrarySeriesAsync(cancellationToken)).Series;
            var byId = library.FirstOrDefault(s => s.Id == seriesId.Value);
            if (byId is not null)
            {
                return byId;
            }

            if (librarySnapshot is not null)
            {
                throw new SuwayomiException($"Suwayomi has no series with id {seriesId.Value} in the supplied library snapshot.");
            }

            // The id may belong to a series that is not in the library (or the
            // library listing was truncated); fall back to a direct lookup.
            return await GetSeriesByIdAsync(seriesId.Value, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            throw new SuwayomiException("A series title or Suwayomi series id is required.");
        }

        // Suwayomi lists a series under its source's title, which can differ
        // from the one shown here (a localized or romanized title, say), so the
        // series' other titles are tried as well. An exact match on any of them
        // is better evidence than a partial match on the shown title, which is
        // often a sequel or spin-off ("Solo Leveling: Ragnarok"), so every title
        // is tried exactly before the shown title's partial matches. The other
        // titles are never matched partially: a series can have dozens, and
        // short ones such as acronyms would match unrelated series and queue
        // their chapters.
        var normalizedTitle = Normalize(seriesTitle);
        var otherTitles = (alternateTitles ?? Enumerable.Empty<string>())
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => (Title: title.Trim(), Key: Normalize(title)))
            .Where(other => other.Key.Length > 0 && other.Key != normalizedTitle)
            .DistinctBy(other => other.Key)
            .ToList();

        librarySnapshot ??= await GetLibrarySeriesAsync(cancellationToken);
        var byTitle = librarySnapshot.NormalizedTitleIndex;
        if (byTitle.TryGetValue(normalizedTitle, out var match))
        {
            return match;
        }

        foreach (var other in otherTitles)
        {
            if (byTitle.TryGetValue(other.Key, out match))
            {
                _logger.LogInformation(
                    "Matched Suwayomi series {SeriesId} ({SuwayomiTitle}) for {SeriesTitle} by its other title {OtherTitle}",
                    match.Id,
                    LoggingHelper.SanitizeForLog(match.Title),
                    LoggingHelper.SanitizeForLog(seriesTitle),
                    LoggingHelper.SanitizeForLog(other.Title));
                return match;
            }
        }

        var partial = RankMatches(librarySnapshot, normalizedTitle).FirstOrDefault();
        if (partial is not null)
        {
            return partial;
        }

        var searched = otherTitles.Count switch
        {
            0 => $"'{seriesTitle}'",
            1 => $"'{seriesTitle}' or its other title '{otherTitles[0].Title}'",
            _ => $"'{seriesTitle}' or any of its {otherTitles.Count} other titles"
        };
        throw new SuwayomiException($"No series matching {searched} was found in the Suwayomi library.");
    }

    private async Task<SuwayomiSeries> GetSeriesByIdAsync(int seriesId, CancellationToken cancellationToken)
    {
        var series = await GetMangaAsync(seriesId, cancellationToken);
        if (!series.InLibrary)
        {
            throw new SuwayomiException($"Suwayomi series with id {seriesId} is not in the library.");
        }

        return series;
    }

    private async Task<SuwayomiSeries> GetMangaAsync(int seriesId, CancellationToken cancellationToken)
    {
        if (seriesId <= 0)
        {
            throw new SuwayomiException($"Suwayomi has no series with id {seriesId}.");
        }

        const string queryText = """
            query Manga($id: Int!) {
              manga(id: $id) { id title inLibrary thumbnailUrl }
            }
            """;

        using var document = await ExecuteAsync(
            "Manga",
            queryText,
            new Dictionary<string, object?> { ["id"] = seriesId },
            cancellationToken);

        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("manga", out var manga)
            || manga.ValueKind != JsonValueKind.Object)
        {
            throw new SuwayomiException($"Suwayomi has no series with id {seriesId}.");
        }

        var series = ReadSeries(manga);
        if (series is null)
        {
            throw new SuwayomiException($"Suwayomi has no series with id {seriesId}.");
        }

        return series;
    }

    private async Task<IReadOnlyList<SuwayomiSeries>> GetLibrarySeriesInternalAsync(CancellationToken cancellationToken)
    {
        // Suwayomi types pagination cursors with its own `Cursor` scalar, and
        // GraphQL validation rejects a `String` variable in that position before
        // the query runs. The cursor still travels as a plain JSON string.
        const string queryText = """
            query Library($first: Int!, $after: Cursor) {
              mangas(condition: { inLibrary: true }, first: $first, after: $after) {
                nodes { id title inLibrary thumbnailUrl }
                pageInfo { hasNextPage endCursor }
              }
            }
            """;

        var series = new List<SuwayomiSeries>();
        string? after = null;
        while (true)
        {
            using var document = await ExecuteAsync(
                "Library",
                queryText,
                new Dictionary<string, object?>
                {
                    ["first"] = MaxLibraryPageSize,
                    ["after"] = after
                },
                cancellationToken);

            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("mangas", out var connection)
                || connection.ValueKind != JsonValueKind.Object
                || !connection.TryGetProperty("nodes", out var nodes)
                || nodes.ValueKind != JsonValueKind.Array)
            {
                throw new SuwayomiException(UnexpectedResponseMessage);
            }

            series.AddRange(nodes.EnumerateArray().Select(ReadSeries).OfType<SuwayomiSeries>());

            if (!connection.TryGetProperty("pageInfo", out var pageInfo)
                || pageInfo.ValueKind != JsonValueKind.Object
                || GetBooleanOrNull(pageInfo, "hasNextPage") is not { } hasNextPage)
            {
                throw new SuwayomiException(UnexpectedResponseMessage);
            }

            if (!hasNextPage)
            {
                return series;
            }

            after = GetStringOrNull(pageInfo, "endCursor");
            if (string.IsNullOrWhiteSpace(after))
            {
                throw new SuwayomiException(UnexpectedResponseMessage);
            }
        }
    }

    private static SuwayomiSeries? ReadSeries(JsonElement node)
    {
        var id = GetInt32OrNull(node, "id");
        if (id is null)
        {
            return null;
        }

        return new SuwayomiSeries
        {
            Id = id.Value,
            Title = GetStringOrNull(node, "title") ?? $"Series {id.Value}",
            InLibrary = GetBooleanOrNull(node, "inLibrary") ?? false,
            ThumbnailUrl = GetStringOrNull(node, "thumbnailUrl")
        };
    }

    /// <summary>
    /// Posts a GraphQL document to the configured server and returns the parsed
    /// response. Throws <see cref="SuwayomiException"/> for every failure mode
    /// (disabled, misconfigured, transport error, HTTP error, GraphQL errors)
    /// so callers have a single thing to catch. <paramref name="operationName"/>
    /// identifies the document in the error logged if Suwayomi rejects it.
    /// <paramref name="timeout"/> replaces the client's usual timeout for a
    /// document that makes Suwayomi query a source's site.
    /// </summary>
    private async Task<JsonDocument> ExecuteAsync(
        string operationName,
        string query,
        IDictionary<string, object?>? variables,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var config = _settings.CurrentValue;
        var endpoint = ResolveEndpoint(config);

        var payload = JsonSerializer.Serialize(
            new { query, variables },
            SerializerOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        ApplyCredentials(request, config);

        using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        if (timeout is { } requestTimeout)
        {
            httpClient.Timeout = requestTimeout;
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Suwayomi request failed");
            throw new SuwayomiException($"Could not reach the Suwayomi server: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Suwayomi request returned HTTP {StatusCode}",
                    (int)response.StatusCode);
                throw new SuwayomiException(
                    $"The Suwayomi server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new SuwayomiException("The Suwayomi server returned a response that was not valid JSON.", ex);
            }

            try
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new SuwayomiException(UnexpectedResponseMessage);
                }

                if (document.RootElement.TryGetProperty("errors", out var errors)
                    && errors.ValueKind == JsonValueKind.Array
                    && errors.GetArrayLength() > 0)
                {
                    var message = errors.EnumerateArray()
                        .Select(e => GetStringOrNull(e, "message"))
                        .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
                        ?? "unknown error";

                    if (IsRequestRejection(errors))
                    {
                        // The documents sent from here are fixed strings, so the
                        // server refusing one is a defect in this integration,
                        // not a problem with the user's server or settings.
                        // Logged at Error so automated error reporting files it;
                        // the exception alone is only shown to the user.
                        //
                        // The operation is spliced into the template rather than
                        // passed as an argument so that each document
                        // fingerprints, and is filed, separately: with one shared
                        // template a later mismatch in another document would
                        // only bump the first issue ever filed. Operation names
                        // are GraphQL names, so they hold no template braces.
                        // Every error is kept, not just the first, because
                        // validation reports all of them at once.
                        var details = string.Join(
                            " | ",
                            errors.EnumerateArray()
                                .Select(e => GetStringOrNull(e, "message"))
                                .Where(m => !string.IsNullOrWhiteSpace(m))
                                .Distinct(StringComparer.Ordinal));

                        _logger.LogError(
                            "Suwayomi rejected the " + operationName
                                + " GraphQL request before running it; the query does not match the server's schema: {GraphQLErrors}",
                            LoggingHelper.SanitizeForLog(details.Length > 0 ? details : message));
                    }

                    throw new SuwayomiException($"The Suwayomi server rejected the request: {message}");
                }

                if (!document.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Object)
                {
                    throw new SuwayomiException(UnexpectedResponseMessage);
                }
            }
            catch
            {
                document.Dispose();
                throw;
            }

            return document;
        }
    }

    /// <summary>
    /// True when the server refused the GraphQL document itself (it failed to
    /// parse or validate, so nothing ran) rather than failing while resolving
    /// a field.
    /// </summary>
    /// <remarks>
    /// The GraphQL spec gives every error tied to a response field a
    /// <c>path</c>, and Suwayomi raises all resolver failures that way,
    /// including its Unauthorized/Forbidden rejections. An error without one
    /// was raised before execution began, which the spec attributes to the
    /// client: a query in this class that the server's schema does not accept,
    /// such as a cursor variable declared as <c>String</c> where Suwayomi
    /// expects its <c>Cursor</c> scalar.
    /// </remarks>
    private static bool IsRequestRejection(JsonElement errors)
        => errors.EnumerateArray().Any(error =>
            error.ValueKind == JsonValueKind.Object
            && !(error.TryGetProperty("path", out var path)
                && path.ValueKind == JsonValueKind.Array
                && path.GetArrayLength() > 0));

    /// <summary>
    /// The GraphQL endpoint of the configured server; throws when the
    /// integration is disabled or the URL is not usable.
    /// </summary>
    private static Uri ResolveEndpoint(AppSettings config)
    {
        if (!config.EnableSuwayomi)
        {
            throw new SuwayomiException("The Suwayomi integration is disabled.");
        }

        if (!TryBuildEndpoint(config.SuwayomiBaseUrl, out var endpoint))
        {
            throw new SuwayomiException("No valid Suwayomi server URL is configured.");
        }

        return endpoint;
    }

    private static void ApplyCredentials(HttpRequestMessage request, AppSettings config)
    {
        if (!string.IsNullOrWhiteSpace(config.SuwayomiUsername))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{config.SuwayomiUsername}:{config.SuwayomiPassword}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
    }

    /// <summary>
    /// Builds the GraphQL endpoint for a configured base URL. Only absolute
    /// http/https URLs are accepted so a malformed value cannot turn into a
    /// request against an unexpected scheme. Public so settings validation (and
    /// tests) can reuse the exact same rule.
    /// </summary>
    public static bool TryBuildEndpoint(string? baseUrl, out Uri endpoint)
    {
        endpoint = null!;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(baseUrl.Trim().TrimEnd('/') + "/api/graphql", UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }

    /// <summary>
    /// Library series whose title matches <paramref name="normalizedQuery"/>,
    /// best first: exact normalized matches, then prefix matches, then any
    /// series whose normalized title contains the query (or vice versa, so
    /// "Series Name Vol. 1" still matches the Suwayomi entry "Series Name").
    /// </summary>
    private static IEnumerable<SuwayomiSeries> RankMatches(
        IEnumerable<SuwayomiSeries> library,
        string normalizedQuery)
        => library
            .Select(series => new { series, score = MatchScore(Normalize(series.Title), normalizedQuery) })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.series.Title, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.series);

    private static IEnumerable<SuwayomiSeries> RankMatches(
        SuwayomiLibrarySnapshot library,
        string normalizedQuery)
        => library.NormalizedSeries
            .Select(item => new
            {
                item.Series,
                score = MatchScore(item.NormalizedTitle, normalizedQuery)
            })
            .Where(item => item.score > 0)
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.Series.Title, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Series);

    /// <summary>
    /// <summary>
    /// Scores how well a Suwayomi title matches the requested one. Higher is
    /// better; 0 means "not a match". An empty query (a title with no letters
    /// or digits) matches nothing rather than prefixing every title.
    /// </summary>
    private static int MatchScore(string candidate, string query)
    {
        if (candidate.Length == 0 || query.Length == 0)
        {
            return 0;
        }

        if (candidate == query)
        {
            return 4;
        }

        if (candidate.StartsWith(query, StringComparison.Ordinal))
        {
            return 3;
        }

        if (candidate.Contains(query, StringComparison.Ordinal))
        {
            return 2;
        }

        if (query.Contains(candidate, StringComparison.Ordinal))
        {
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Lower-cases <paramref name="value"/> and keeps only letters and digits,
    /// so titles that differ only in punctuation or spacing compare equal.
    /// </summary>
    internal static string Normalize(string value)
    {
        var filtered = value
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray();

        return new string(filtered);
    }

    private static string? GetStringOrNull(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static int? GetInt32OrNull(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        // Suwayomi returns ids as numbers, but some clients/proxies surface
        // them as strings; accept both.
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>
    /// Reads a 64-bit source id, which Suwayomi's LongString scalar sends as
    /// a string of digits, in canonical form; null for anything else.
    /// </summary>
    private static string? GetSourceIdOrNull(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static double? GetDoubleOrNull(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null
        };
    }

    private static bool? GetBooleanOrNull(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
