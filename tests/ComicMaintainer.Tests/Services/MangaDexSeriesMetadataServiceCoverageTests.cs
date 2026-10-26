using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public sealed class MangaDexSeriesMetadataServiceCoverageTests : IDisposable
{
    private const string BaseUrl = "https://api.mangadex.example";
    private const string ProbeCacheKey = "mangadex-health-probe";
    private const string RateLimitHeader = "X-RateLimit-Retry-After";
    private const string BackOffMessage = "rate limit hit";
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly ListLogger _logger = new();

    public void Dispose() => _cache.Dispose();

    private static AppSettings Settings(bool enabled = true, string baseUrl = BaseUrl + "/") => new()
    {
        EnableMangaDexMetadata = enabled,
        MangaDexBaseUrl = baseUrl,
        // Keep the client-side limiter out of the way so no test waits on it.
        MangaDexRequestsPerSecond = 1000
    };

    private MangaDexSeriesMetadataService CreateService(StubHandler handler, AppSettings? settings = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        var options = new Mock<IOptionsMonitor<AppSettings>>();
        options.Setup(o => o.CurrentValue).Returns(settings ?? Settings());
        return new MangaDexSeriesMetadataService(factory.Object, options.Object, _cache, _logger);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage TooManyRequests(string retryAfterCase)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var now = DateTimeOffset.UtcNow;
        var unixNow = now.ToUnixTimeSeconds();
        string Unix(long seconds) => seconds.ToString(CultureInfo.InvariantCulture);
        switch (retryAfterCase)
        {
            case "delta-3600":
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
                break;
            case "delta-0":
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                break;
            case "date-future":
                response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(60));
                break;
            case "date-past":
                response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddMinutes(-5));
                break;
            case "unix-future":
                response.Headers.TryAddWithoutValidation(RateLimitHeader, Unix(unixNow + 30));
                break;
            case "unix-garbage":
                response.Headers.TryAddWithoutValidation(RateLimitHeader, "soon");
                break;
            case "unix-past":
                response.Headers.TryAddWithoutValidation(RateLimitHeader, Unix(unixNow - 30));
                break;
            case "unix-garbage-then-future":
                response.Headers.TryAddWithoutValidation(RateLimitHeader, new[] { "soon", Unix(unixNow + 30) });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(retryAfterCase), retryAfterCase, null);
        }
        return response;
    }

    /// <summary>
    /// Cancels <paramref name="cts"/> as soon as the invoker announces its 429
    /// back-off so the production <c>Task.Delay</c> ends immediately.
    /// </summary>
    private void CancelOnBackOff(CancellationTokenSource cts)
        => _logger.OnLog = entry =>
        {
            if (entry.Message.Contains(BackOffMessage, StringComparison.Ordinal))
            {
                cts.Cancel();
            }
        };

    /// <summary>
    /// Runs a search that <see cref="CancelOnBackOff"/> cancels during the 429
    /// back-off: the caller's cancellation surfaces instead of a "no match".
    /// </summary>
    private static Task SearchCancelledDuringTheBackOffAsync(
        MangaDexSeriesMetadataService service,
        string query,
        CancellationToken cancellationToken)
        => Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SearchSeriesAsync(query, cancellationToken: cancellationToken));

    [Fact]
    public void ProviderName_IsMangaDex()
    {
        Assert.Equal("MangaDex", CreateService(new StubHandler(_ => Json("{}"))).ProviderName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LookupAndSearch_BlankInput_ReturnNothingWithoutCallingTheApi(string input)
    {
        var handler = new StubHandler(_ => Json("""{"data":[]}"""));
        var service = CreateService(handler);

        Assert.Null(await service.LookupSeriesAsync(input));
        Assert.Empty(await service.SearchSeriesAsync(input));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SearchSeriesAsync_Disabled_ReturnsEmptyWithoutCallingTheApi()
    {
        var handler = new StubHandler(_ => Json("""{"data":[]}"""));
        var service = CreateService(handler, Settings(enabled: false));

        Assert.Empty(await service.SearchSeriesAsync("Solo Leveling"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LookupSeriesAsync_CachesMatches_UnderTheNormalizedName()
    {
        var handler = new StubHandler(_ => Json("""{"data":[{"id":"1","attributes":{"title":{"en":"Solo Leveling"}}}]}"""));
        var service = CreateService(handler);

        var first = await service.LookupSeriesAsync("Solo Leveling");
        var second = await service.LookupSeriesAsync("  SOLO-leveling!! ");

        Assert.NotNull(first);
        Assert.Equal("Solo Leveling", first.CanonicalTitle);
        Assert.Same(first, second);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(25, 25)]
    [InlineData(500, 50)]
    public async Task SearchSeriesAsync_ClampsLimit_AndRequestsCoverArt(int requested, int expected)
    {
        var handler = new StubHandler(_ => Json("""
        {"data":[
          {"id":"1","attributes":{"title":{"en":"Solo Leveling"}}},
          {"id":"2","attributes":{"title":{"en":"Solo Leveling: Ragnarok"}}}
        ]}
        """));
        var service = CreateService(handler);

        var results = await service.SearchSeriesAsync("Solo Leveling", requested);

        Assert.Equal(new[] { "Solo Leveling", "Solo Leveling: Ragnarok" }, results.Select(r => r.CanonicalTitle));
        Assert.All(results, r => Assert.Equal("MangaDex", r.Source));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            $"{BaseUrl}/manga?title=Solo%20Leveling&limit={expected}&includes[]=cover_art",
            request.Uri);
        Assert.Null(request.Accept);
    }

    [Theory]
    [InlineData("""{"result":"error"}""")]
    [InlineData("""{"data":{"id":"1"}}""")]
    public async Task SearchSeriesAsync_ResponseWithoutDataArray_ReturnsEmptyAsASuccessfulCall(string json)
    {
        var service = CreateService(new StubHandler(_ => Json(json)));

        Assert.Empty(await service.SearchSeriesAsync("Solo Leveling"));

        Assert.Empty(_logger.Entries);
        _cache.Set(ProbeCacheKey, (true, "Reachable"));
        var health = await service.CheckHealthAsync();
        Assert.Equal(1, health.SuccessCount);
        Assert.Equal(0, health.FailureCount);
    }

    [Fact]
    public async Task SearchSeriesAsync_MalformedJson_LogsWarningAndRecordsFailure()
    {
        var service = CreateService(new StubHandler(_ => Json("{ not json")));

        Assert.Empty(await service.SearchSeriesAsync("Solo Leveling"));

        var warning = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal("MangaDex lookup failed for Solo Leveling", warning.Message);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(warning.Exception);
        _cache.Set(ProbeCacheKey, (true, "Reachable"));
        var health = await service.CheckHealthAsync();
        Assert.Equal(1, health.FailureCount);
        Assert.Equal(warning.Exception.Message, health.LastError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LookupAndSearch_CallerCancelsMidRequest_RethrowWithoutLoggingOrRecordingAFailure(bool lookup)
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var service = CreateService(handler);
        Func<Task> call = lookup
            ? () => service.LookupSeriesAsync("Solo Leveling", cts.Token)
            : () => service.SearchSeriesAsync("Solo Leveling", cancellationToken: cts.Token);

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(call);

        Assert.Equal(cts.Token, cancelled.CancellationToken);
        Assert.Single(handler.Requests);
        Assert.Empty(_logger.Entries);
        _cache.Set(ProbeCacheKey, (true, "Reachable"));
        var health = await service.CheckHealthAsync();
        Assert.Equal(0, health.FailureCount);
        Assert.Null(health.LastError);
    }

    [Fact]
    public async Task SearchSeriesAsync_RequestTimesOut_ReturnsEmptyLogsAndRecordsTheFailure()
    {
        // A cancellation the caller didn't ask for, which is how HttpClient reports its timeout.
        var service = CreateService(new StubHandler(_ => throw new TaskCanceledException("timed out")));

        Assert.Empty(await service.SearchSeriesAsync("Solo Leveling"));

        var warning = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal("MangaDex lookup failed for Solo Leveling", warning.Message);
        Assert.IsType<TaskCanceledException>(warning.Exception);
        _cache.Set(ProbeCacheKey, (true, "Reachable"));
        var health = await service.CheckHealthAsync();
        Assert.Equal(1, health.FailureCount);
        Assert.Equal(warning.Exception.Message, health.LastError);
    }

    [Fact]
    public async Task SearchSeriesAsync_SkipsEntriesWithoutTitles_AndKeepsBlankLanguageAltTitlesAsNonEnglish()
    {
        const string json = """
        {
          "data": [
            { "id": "no-attributes" },
            { "id": "no-titles", "attributes": {} },
            {
              "id": "mixed",
              "attributes": {
                "title": { "en": null, "ja-ro": "Ore dake Level Up na Ken" },
                "altTitles": [ { "": "Blank Language Title" }, { "en-us": "Solo Leveling" } ]
              }
            }
          ]
        }
        """;
        var service = CreateService(new StubHandler(_ => Json(json)));

        var result = Assert.Single(await service.SearchSeriesAsync("Solo Leveling"));

        Assert.Equal("Solo Leveling", result.CanonicalTitle);
        Assert.Equal(new[] { "Ore dake Level Up na Ken", "Blank Language Title" }, result.Aliases);
        Assert.Equal(
            new (string, string?)[] { ("Solo Leveling", "en-us"), ("Ore dake Level Up na Ken", "ja-ro"), ("Blank Language Title", "") },
            result.LocalizedTitles.Select(t => (t.Title, t.Language)));
    }

    [Fact]
    public async Task SearchSeriesAsync_DescriptionFallsBackToTheFirstNonBlankLanguage()
    {
        const string json = """
        {
          "data": [
            { "id": "1", "attributes": { "title": { "en": "Blank English" }, "description": { "en": "  ", "ja": 5, "ko": "", "fr": "Résumé français" } } },
            { "id": "2", "attributes": { "title": { "en": "No English Key" }, "description": { "ja": "日本語の説明" } } },
            { "id": "3", "attributes": { "title": { "en": "Null English" }, "description": { "en": null, "es": "Descripción" } } },
            { "id": "4", "attributes": { "title": { "en": "Nothing Usable" }, "description": { "en": "", "ja": 1 } } },
            { "id": "5", "attributes": { "title": { "en": "English Wins" }, "description": { "ja": "後", "en": "English text" } } }
          ]
        }
        """;
        var service = CreateService(new StubHandler(_ => Json(json)));

        var results = await service.SearchSeriesAsync("anything");

        Assert.Equal(
            new[] { "Résumé français", "日本語の説明", "Descripción", null, "English text" },
            results.Select(r => r.Synopsis));
    }

    [Fact]
    public async Task SearchSeriesAsync_CoverUrlsRequireANonBlankIdAndARelationshipsArray()
    {
        const string json = """
        {
          "data": [
            { "id": "", "attributes": { "title": { "en": "Blank Id" } }, "relationships": [ { "type": "cover_art", "attributes": { "fileName": "cover.jpg" } } ] },
            { "id": "no-rels", "attributes": { "title": { "en": "No Relationships" } } },
            { "id": "object-rels", "attributes": { "title": { "en": "Object Relationships" } }, "relationships": { "type": "cover_art", "attributes": { "fileName": "cover.jpg" } } },
            { "id": "abc-123", "attributes": { "title": { "en": "With Cover" } }, "relationships": [ { "type": "author" }, { "type": "cover_art", "attributes": { "fileName": "cover.jpg" } } ] }
          ]
        }
        """;
        var service = CreateService(new StubHandler(_ => Json(json)));

        var results = await service.SearchSeriesAsync("anything");

        Assert.Equal(4, results.Count);
        Assert.All(results.Take(3), r =>
        {
            Assert.Null(r.ImageUrl);
            Assert.Null(r.ThumbnailUrl);
        });
        Assert.Equal("https://uploads.mangadex.org/covers/abc-123/cover.jpg", results[3].ImageUrl);
        Assert.Equal("https://uploads.mangadex.org/covers/abc-123/cover.jpg.256.jpg", results[3].ThumbnailUrl);
    }

    public static TheoryData<string, double, double> RetryAfterCases => new()
    {
        { "delta-3600", 120, 120 },
        { "delta-0", 0.25, 0.25 },
        { "date-future", 50, 60.5 },
        { "date-past", 5, 5 },
        { "unix-future", 25, 30.5 },
        { "unix-garbage", 5, 5 },
        { "unix-past", 5, 5 },
        { "unix-garbage-then-future", 25, 30.5 },
    };

    [Theory]
    [MemberData(nameof(RetryAfterCases))]
    public async Task SearchSeriesAsync_Http429_BacksOffForTheClampedServerInterval(
        string retryAfterCase,
        double minSeconds,
        double maxSeconds)
    {
        using var cts = new CancellationTokenSource();
        CancelOnBackOff(cts);
        var handler = new StubHandler(_ => TooManyRequests(retryAfterCase));
        var service = CreateService(handler);

        await SearchCancelledDuringTheBackOffAsync(service, "Solo Leveling", cts.Token);

        var backOff = Assert.Single(_logger.Entries, e => e.Message.Contains(BackOffMessage, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, backOff.Level);
        Assert.Equal("MangaDex", backOff.Properties["Provider"]);
        var seconds = Assert.IsType<double>(backOff.Properties["RetryAfterSeconds"]);
        Assert.InRange(seconds, minSeconds, maxSeconds);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CheckHealthAsync_CachedProbeDuringRateLimitWindow_ReportsDegraded()
    {
        using var cts = new CancellationTokenSource();
        CancelOnBackOff(cts);
        var handler = new StubHandler(_ => TooManyRequests("delta-3600"));
        var service = CreateService(handler);
        var before = DateTime.UtcNow;
        await SearchCancelledDuringTheBackOffAsync(service, "Solo Leveling", cts.Token);
        _cache.Set(ProbeCacheKey, (true, "Reachable"));

        var health = await service.CheckHealthAsync();

        Assert.True(health.Enabled);
        Assert.True(health.Configured);
        Assert.True(health.Reachable);
        Assert.True(health.RateLimited);
        Assert.Equal("Degraded - rate limited", health.StatusMessage);
        Assert.NotNull(health.RateLimitedUntilUtc);
        Assert.InRange(health.RateLimitedUntilUtc.Value, before.AddSeconds(119), DateTime.UtcNow.AddSeconds(121));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CheckHealthAsync_CachedProbe_IsReportedWithoutProbing()
    {
        var handler = new StubHandler(_ => Json("""{"data":[]}"""));
        _cache.Set(ProbeCacheKey, (false, "HTTP 503 Service Unavailable"));
        var service = CreateService(handler);

        var health = await service.CheckHealthAsync();

        Assert.False(health.Reachable);
        Assert.False(health.RateLimited);
        Assert.Equal("HTTP 503 Service Unavailable", health.StatusMessage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CheckHealthAsync_NullCachedProbe_IsIgnoredAndProbedAgain()
    {
        var handler = new StubHandler(_ => Json("""{"data":[]}"""));
        _cache.Set<object?>(ProbeCacheKey, null);
        var service = CreateService(handler);

        var health = await service.CheckHealthAsync();

        Assert.True(health.Reachable);
        Assert.Equal("Reachable", health.StatusMessage);
        var probe = Assert.Single(handler.Requests);
        Assert.Equal($"{BaseUrl}/manga?title=ping&limit=1&includes[]=cover_art", probe.Uri);
        Assert.Equal("application/json", probe.Accept);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CheckHealthAsync_MissingBaseUrl_ReportsNotConfiguredWithoutProbing(string baseUrl)
    {
        var handler = new StubHandler(_ => Json("""{"data":[]}"""));
        var service = CreateService(handler, Settings(baseUrl: baseUrl));

        var health = await service.CheckHealthAsync();

        Assert.True(health.Enabled);
        Assert.False(health.Configured);
        Assert.Null(health.Reachable);
        Assert.Equal("Missing base URL", health.StatusMessage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CheckHealthAsync_ProbeStillRateLimitedAfterTheRetry_ReportsRateLimited()
    {
        // Retry-After: 0 is floored to the invoker's 250 ms minimum back-off,
        // which is the only real wait in this class.
        var handler = new StubHandler(_ => TooManyRequests("delta-0"));
        var service = CreateService(handler);

        var health = await service.CheckHealthAsync();

        Assert.False(health.Reachable);
        Assert.Equal("Rate limited (HTTP 429)", health.StatusMessage);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CheckHealthAsync_ProbeTimeout_ReportsTimedOut()
    {
        var service = CreateService(new StubHandler(_ => throw new TaskCanceledException("probe timed out")));

        var health = await service.CheckHealthAsync();

        Assert.False(health.Reachable);
        Assert.Equal("Timed out", health.StatusMessage);
    }

    [Fact]
    public async Task CheckHealthAsync_ProbeNetworkError_ReportsNetworkError()
    {
        var service = CreateService(new StubHandler(_ => throw new HttpRequestException("connection refused")));

        var health = await service.CheckHealthAsync();

        Assert.False(health.Reachable);
        Assert.Equal("Network error: connection refused", health.StatusMessage);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<(string Uri, string? Accept)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                var accept = request.Headers.Accept.Count == 0 ? null : request.Headers.Accept.ToString();
                Requests.Add((request.RequestUri!.OriginalString, accept));
            }
            return Task.FromResult(_respond(request));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class ListLogger : ILogger<MangaDexSeriesMetadataService>
    {
        private readonly List<LogEntry> _entries = new();

        public Action<LogEntry>? OnLog { get; set; }

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value)
                : new Dictionary<string, object?>();
            var entry = new LogEntry(logLevel, formatter(state, exception), exception, properties);
            lock (_entries)
            {
                _entries.Add(entry);
            }
            OnLog?.Invoke(entry);
        }
    }
}
