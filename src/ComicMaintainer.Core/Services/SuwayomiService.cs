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
/// issues a locally-tracked series is missing.
/// </summary>
/// <remarks>
/// Only series that are already in the Suwayomi library are considered:
/// Suwayomi only keeps a chapter list (and therefore downloadable chapters)
/// for library entries, and queueing downloads for arbitrary search hits would
/// require adding them to the library first, which is a decision for the
/// Suwayomi side to make.
/// </remarks>
public class SuwayomiService : ISuwayomiService
{
    public const string HttpClientName = "Suwayomi";

    private const int MaxLibraryPageSize = 500;

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
                throw new SuwayomiException("The Suwayomi server returned an unexpected response.");
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

        var chapters = new List<SuwayomiChapter>();
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("chapters", out var connection)
            || !connection.TryGetProperty("nodes", out var nodes)
            || nodes.ValueKind != JsonValueKind.Array)
        {
            return chapters;
        }

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

        return chapters
            .OrderBy(c => c.ChapterNumber)
            .ToList();
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

        var series = await ResolveSeriesAsync(
            seriesTitle, alternateTitles, seriesId, librarySnapshot, cancellationToken);
        var chapters = await GetChaptersAsync(series.Id, cancellationToken);

        var result = new SuwayomiDownloadResult { Series = series };
        var chapterIds = new List<int>();

        foreach (var issueNumber in requested)
        {
            // A chapter covers an issue number when its (floored) chapter
            // number matches, which mirrors how the library treats decimal
            // "half chapters" as belonging to their whole number.
            var candidates = chapters
                .Where(c => c.ChapterNumber >= 0 && (int)Math.Floor(c.ChapterNumber) == issueNumber)
                .ToList();

            if (candidates.Count == 0)
            {
                result.UnmatchedIssueNumbers.Add(issueNumber);
                continue;
            }

            var pending = candidates.Where(c => !c.IsDownloaded).ToList();
            if (pending.Count == 0)
            {
                result.AlreadyDownloadedIssueNumbers.Add(issueNumber);
                continue;
            }

            foreach (var chapter in pending)
            {
                if (chapterIds.Contains(chapter.Id))
                {
                    continue;
                }

                chapterIds.Add(chapter.Id);
                result.QueuedChapters.Add(chapter);
            }
        }

        if (chapterIds.Count == 0)
        {
            return result;
        }

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

        _logger.LogInformation(
            "Queued {Count} Suwayomi chapter download(s) for series {SeriesId} ({SeriesTitle})",
            chapterIds.Count,
            series.Id,
            LoggingHelper.SanitizeForLog(series.Title));

        return result;
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

        if (!series.InLibrary)
        {
            throw new SuwayomiException($"Suwayomi series with id {seriesId} is not in the library.");
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
                throw new SuwayomiException("The Suwayomi server returned an unexpected response.");
            }

            foreach (var node in nodes.EnumerateArray())
            {
                var entry = ReadSeries(node);
                if (entry is not null)
                {
                    series.Add(entry);
                }
            }

            if (!connection.TryGetProperty("pageInfo", out var pageInfo)
                || pageInfo.ValueKind != JsonValueKind.Object
                || GetBooleanOrNull(pageInfo, "hasNextPage") is not { } hasNextPage)
            {
                throw new SuwayomiException("The Suwayomi server returned an unexpected response.");
            }

            if (!hasNextPage)
            {
                return series;
            }

            after = GetStringOrNull(pageInfo, "endCursor");
            if (string.IsNullOrWhiteSpace(after))
            {
                throw new SuwayomiException("The Suwayomi server returned an unexpected response.");
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
    /// </summary>
    private async Task<JsonDocument> ExecuteAsync(
        string operationName,
        string query,
        IDictionary<string, object?>? variables,
        CancellationToken cancellationToken)
    {
        var config = _settings.CurrentValue;
        if (!config.EnableSuwayomi)
        {
            throw new SuwayomiException("The Suwayomi integration is disabled.");
        }

        if (!TryBuildEndpoint(config.SuwayomiBaseUrl, out var endpoint))
        {
            throw new SuwayomiException("No valid Suwayomi server URL is configured.");
        }

        var payload = JsonSerializer.Serialize(
            new { query, variables },
            SerializerOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(config.SuwayomiUsername))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{config.SuwayomiUsername}:{config.SuwayomiPassword}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        using var httpClient = _httpClientFactory.CreateClient(HttpClientName);

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
                    throw new SuwayomiException("The Suwayomi server returned an unexpected response.");
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
                    throw new SuwayomiException("The Suwayomi server returned an unexpected response.");
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

    private static string Normalize(string value)
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
