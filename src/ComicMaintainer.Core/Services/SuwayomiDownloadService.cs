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
/// Talks to a Suwayomi (Tachidesk) sidecar over its GraphQL API
/// (<c>POST {base}/api/graphql</c>) to enqueue downloads of missing issues.
///
/// The source/extension used for a download is implicitly the one the matching
/// series is already bound to in Suwayomi's library: we locate the tracked
/// manga whose title matches one of our series' titles, list its chapters, and
/// ask Suwayomi to download the chapters whose chapter number matches the
/// requested issue numbers. The actual downloading happens inside Suwayomi.
/// </summary>
public sealed class SuwayomiDownloadService : ISuwayomiDownloadService
{
    public const string HttpClientName = nameof(SuwayomiDownloadService);

    // Minimum title-match confidence required before we will act on a tracked
    // Suwayomi series. Mirrors the substring/near-exact band of SeriesMatchScorer
    // so we don't download the wrong series on a loose fuzzy match.
    private const double MinMatchScore = 80d;

    // Epsilon for comparing floating-point chapter numbers to requested issues.
    private const double NumberEpsilon = 0.001d;

    private const string LibraryMangaQuery = """
        query ($inLibrary: Boolean) {
          mangas(condition: { inLibrary: $inLibrary }) {
            nodes {
              id
              title
              sourceId
              source { displayName }
            }
          }
        }
        """;

    private const string ChaptersQuery = """
        query ($mangaId: Int) {
          chapters(condition: { mangaId: $mangaId }) {
            nodes {
              id
              name
              chapterNumber
              isDownloaded
            }
          }
        }
        """;

    private const string EnqueueMutation = """
        mutation ($ids: [Int!]!) {
          enqueueChapterDownloads(input: { ids: $ids }) {
            clientMutationId
          }
        }
        """;

    private const string ProbeQuery = "query { aboutServer { name version } }";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<SuwayomiDownloadService> _logger;
    private readonly ProviderHealthTracker _health = new("Suwayomi");
    private readonly Lazy<ProviderRateLimiter> _rateLimiter;

    public SuwayomiDownloadService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppSettings> settings,
        ILogger<SuwayomiDownloadService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
        _rateLimiter = new Lazy<ProviderRateLimiter>(
            () => new ProviderRateLimiter(
                "Suwayomi",
                Math.Max(1, _settings.CurrentValue.SuwayomiRequestsPerSecond),
                TimeSpan.FromSeconds(1)),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<SuwayomiAvailability> GetAvailabilityAsync(
        bool probe = false,
        CancellationToken cancellationToken = default)
    {
        var config = _settings.CurrentValue;
        var enabled = config.EnableSuwayomiDownloads;
        var configured = TryBuildEndpoint(config, out _, out _);

        if (!enabled)
        {
            return new SuwayomiAvailability
            {
                Enabled = false,
                Configured = configured,
                StatusMessage = "Suwayomi downloads are disabled"
            };
        }

        if (!configured)
        {
            return new SuwayomiAvailability
            {
                Enabled = true,
                Configured = false,
                StatusMessage = "Suwayomi base URL is not configured or is invalid"
            };
        }

        if (!probe)
        {
            return new SuwayomiAvailability
            {
                Enabled = true,
                Configured = true,
                StatusMessage = "Configured"
            };
        }

        try
        {
            using var document = await ExecuteGraphQlAsync(config, ProbeQuery, variables: null, cancellationToken);
            var reachable = document is not null;
            return new SuwayomiAvailability
            {
                Enabled = true,
                Configured = true,
                Reachable = reachable,
                StatusMessage = reachable ? "Reachable" : "Suwayomi returned an unexpected response"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SuwayomiAvailability
            {
                Enabled = true,
                Configured = true,
                Reachable = false,
                StatusMessage = $"Unreachable: {ex.Message}"
            };
        }
    }

    public async Task<SuwayomiDownloadResult> DownloadIssuesAsync(
        IReadOnlyList<string> candidateTitles,
        IReadOnlyList<string> issueNumbers,
        CancellationToken cancellationToken = default)
    {
        var config = _settings.CurrentValue;
        if (!config.EnableSuwayomiDownloads)
        {
            return SuwayomiDownloadResult.Failed("Suwayomi downloads are disabled");
        }

        if (!TryBuildEndpoint(config, out _, out var endpointError))
        {
            return SuwayomiDownloadResult.Failed(endpointError ?? "Suwayomi base URL is not configured");
        }

        var titles = (candidateTitles ?? Array.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (titles.Count == 0)
        {
            return SuwayomiDownloadResult.Failed("No series titles available to match against Suwayomi");
        }

        // Parse and de-duplicate the requested issue numbers.
        var requested = new List<(string Raw, double Value)>();
        foreach (var raw in issueNumbers ?? Array.Empty<string>())
        {
            if (TryParseNumber(raw, out var value)
                && !requested.Any(r => Math.Abs(r.Value - value) < NumberEpsilon))
            {
                requested.Add((raw.Trim(), value));
            }
        }
        if (requested.Count == 0)
        {
            return SuwayomiDownloadResult.Failed("No valid issue numbers to download");
        }

        SuwayomiSeriesMatch? match;
        IReadOnlyList<SuwayomiChapter> chapters;
        try
        {
            match = await FindLibraryMatchAsync(config, titles, cancellationToken);
            if (match is null)
            {
                return SuwayomiDownloadResult.Failed(
                    "No matching series found in Suwayomi's library. Add and track the series in Suwayomi first.");
            }

            chapters = await GetChaptersAsync(config, match.MangaId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Suwayomi download lookup failed");
            return SuwayomiDownloadResult.Failed($"Failed to query Suwayomi: {ex.Message}");
        }

        // Map each requested issue to a chapter by chapter number.
        var toEnqueue = new List<int>();
        var perIssue = new List<SuwayomiIssueDownloadResult>();
        foreach (var (raw, value) in requested)
        {
            var chapter = chapters.FirstOrDefault(c => Math.Abs(c.ChapterNumber - value) < NumberEpsilon);
            if (chapter is null)
            {
                perIssue.Add(new SuwayomiIssueDownloadResult
                {
                    Issue = raw,
                    Enqueued = false,
                    Status = "Not available from the matched Suwayomi source"
                });
                continue;
            }

            if (chapter.IsDownloaded)
            {
                perIssue.Add(new SuwayomiIssueDownloadResult
                {
                    Issue = raw,
                    Enqueued = false,
                    Status = "Already downloaded in Suwayomi"
                });
                continue;
            }

            toEnqueue.Add(chapter.Id);
            perIssue.Add(new SuwayomiIssueDownloadResult
            {
                Issue = raw,
                Enqueued = true,
                Status = "Queued for download"
            });
        }

        if (toEnqueue.Count == 0)
        {
            return new SuwayomiDownloadResult
            {
                Success = perIssue.Any(i => i.Status == "Already downloaded in Suwayomi"),
                Message = "Nothing to queue (matched chapters are unavailable or already downloaded)",
                Match = match,
                Issues = perIssue
            };
        }

        try
        {
            await EnqueueDownloadsAsync(config, toEnqueue, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Suwayomi enqueue failed for manga {MangaId}", match.MangaId);
            return new SuwayomiDownloadResult
            {
                Success = false,
                Message = $"Failed to queue downloads in Suwayomi: {ex.Message}",
                Match = match,
                Issues = perIssue.Select(i => i.Enqueued
                    ? new SuwayomiIssueDownloadResult { Issue = i.Issue, Enqueued = false, Status = "Failed to queue" }
                    : i).ToList()
            };
        }

        return new SuwayomiDownloadResult
        {
            Success = true,
            Message = $"Queued {toEnqueue.Count} chapter(s) in Suwayomi from source '{match.SourceName ?? "unknown"}'",
            Match = match,
            Issues = perIssue
        };
    }

    private async Task<SuwayomiSeriesMatch?> FindLibraryMatchAsync(
        AppSettings config,
        IReadOnlyList<string> titles,
        CancellationToken cancellationToken)
    {
        using var document = await ExecuteGraphQlAsync(
            config,
            LibraryMangaQuery,
            new Dictionary<string, object?> { ["inLibrary"] = true },
            cancellationToken);
        if (document is null)
        {
            return null;
        }

        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("mangas", out var mangas)
            || !mangas.TryGetProperty("nodes", out var nodes)
            || nodes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        SuwayomiSeriesMatch? best = null;
        foreach (var node in nodes.EnumerateArray())
        {
            var title = node.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(title) || !node.TryGetProperty("id", out var idEl))
            {
                continue;
            }

            var score = titles.Max(candidate => SeriesMatchScorer.Score(candidate, title, null));
            if (score < MinMatchScore || (best is not null && score <= best.MatchScore))
            {
                continue;
            }

            best = new SuwayomiSeriesMatch
            {
                MangaId = idEl.GetInt32(),
                Title = title,
                SourceId = node.TryGetProperty("sourceId", out var s) && s.ValueKind == JsonValueKind.Number
                    ? s.GetInt64()
                    : 0,
                SourceName = node.TryGetProperty("source", out var src)
                             && src.ValueKind == JsonValueKind.Object
                             && src.TryGetProperty("displayName", out var dn)
                    ? dn.GetString()
                    : null,
                MatchScore = score
            };
        }

        return best;
    }

    private async Task<IReadOnlyList<SuwayomiChapter>> GetChaptersAsync(
        AppSettings config,
        int mangaId,
        CancellationToken cancellationToken)
    {
        using var document = await ExecuteGraphQlAsync(
            config,
            ChaptersQuery,
            new Dictionary<string, object?> { ["mangaId"] = mangaId },
            cancellationToken);
        if (document is null
            || !document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("chapters", out var chapters)
            || !chapters.TryGetProperty("nodes", out var nodes)
            || nodes.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SuwayomiChapter>();
        }

        var results = new List<SuwayomiChapter>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("id", out var idEl))
            {
                continue;
            }

            results.Add(new SuwayomiChapter
            {
                Id = idEl.GetInt32(),
                Name = node.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                ChapterNumber = node.TryGetProperty("chapterNumber", out var cn) && cn.ValueKind == JsonValueKind.Number
                    ? cn.GetDouble()
                    : double.NaN,
                IsDownloaded = node.TryGetProperty("isDownloaded", out var dl)
                               && dl.ValueKind == JsonValueKind.True
            });
        }

        return results;
    }

    private async Task EnqueueDownloadsAsync(
        AppSettings config,
        IReadOnlyList<int> chapterIds,
        CancellationToken cancellationToken)
    {
        using var document = await ExecuteGraphQlAsync(
            config,
            EnqueueMutation,
            new Dictionary<string, object?> { ["ids"] = chapterIds },
            cancellationToken);
        if (document is null)
        {
            throw new InvalidOperationException("Suwayomi returned no response to the download request");
        }

        if (document.RootElement.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0)
        {
            var message = errors[0].TryGetProperty("message", out var m) ? m.GetString() : null;
            throw new InvalidOperationException(message ?? "Suwayomi rejected the download request");
        }
    }

    /// <summary>
    /// Executes a GraphQL request and returns the parsed JSON document, or null
    /// when the response was not a successful HTTP 2xx. Throws on transport
    /// errors. Caller owns the returned <see cref="JsonDocument"/>.
    /// </summary>
    private async Task<JsonDocument?> ExecuteGraphQlAsync(
        AppSettings config,
        string query,
        IDictionary<string, object?>? variables,
        CancellationToken cancellationToken)
    {
        if (!TryBuildEndpoint(config, out var endpoint, out _))
        {
            return null;
        }

        using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await RateLimitedHttpInvoker.SendAsync(
            httpClient,
            () => BuildGraphQlRequest(config, endpoint!, query, variables),
            _rateLimiter.Value,
            _health,
            _logger,
            "Suwayomi",
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _health.RecordFailure($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            _logger.LogWarning(
                "Suwayomi GraphQL request failed with status {StatusCode}",
                (int)response.StatusCode);
            throw new HttpRequestException($"Suwayomi responded with HTTP {(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        _health.RecordSuccess();
        return document;
    }

    private static HttpRequestMessage BuildGraphQlRequest(
        AppSettings config,
        Uri endpoint,
        string query,
        IDictionary<string, object?>? variables)
    {
        var payload = new Dictionary<string, object?> { ["query"] = query };
        if (variables is not null)
        {
            payload["variables"] = variables;
        }

        var json = JsonSerializer.Serialize(payload);
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrEmpty(config.SuwayomiUsername))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{config.SuwayomiUsername}:{config.SuwayomiPassword}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        return request;
    }

    /// <summary>
    /// Builds the GraphQL endpoint URI from the configured base URL, requiring an
    /// absolute http/https URL to avoid using an unexpected scheme.
    /// </summary>
    private static bool TryBuildEndpoint(AppSettings config, out Uri? endpoint, out string? error)
    {
        endpoint = null;
        error = null;

        var baseUrl = config.SuwayomiBaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            error = "Suwayomi base URL is not configured";
            return false;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            error = "Suwayomi base URL must be an absolute http(s) URL";
            return false;
        }

        var trimmed = baseUrl.TrimEnd('/');
        // Allow the user to point either at the server root or directly at the
        // GraphQL endpoint.
        var combined = trimmed.EndsWith("/api/graphql", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + "/api/graphql";

        if (!Uri.TryCreate(combined, UriKind.Absolute, out endpoint))
        {
            error = "Could not construct the Suwayomi GraphQL endpoint URL";
            return false;
        }

        return true;
    }

    private static bool TryParseNumber(string? raw, out double value)
    {
        value = 0d;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return double.TryParse(
            raw.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
    }
}
