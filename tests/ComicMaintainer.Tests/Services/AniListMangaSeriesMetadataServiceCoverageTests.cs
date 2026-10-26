using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <remarks>
/// Both AniList services draw every request from one process-wide limiter
/// (28 per minute) and the existing AniList tests already spend 24 of those
/// permits, so this class and its Manhwa counterpart send two stubbed requests
/// each and reach every other path through a client factory that throws or
/// through pre-populated cache entries.
/// </remarks>
[Collection(AniListMangaSeriesMetadataServiceCoverageTests.LimiterBudgetCollection.Name)]
public sealed class AniListMangaSeriesMetadataServiceCoverageTests : IDisposable
{
    private const string BaseUrl = "https://graphql.anilist.example";
    private const string ProbeCacheKey = "anilist-manga-health-probe";
    private const string BackOffMessage = "rate limit hit";
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly Mock<IHttpClientFactory> _factory = new();
    private readonly ListLogger _logger = new();

    public void Dispose() => _cache.Dispose();

    /// <summary>
    /// xUnit runs non-parallel collections after every parallel one has
    /// finished, so these requests never compete with the existing AniList
    /// tests (whose health probes time out after 5s) for the shared permits.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class LimiterBudgetCollection
    {
        public const string Name = "AniListManga coverage (shared AniList rate limiter)";
    }

    private static AppSettings Settings(bool enabled = true, string baseUrl = BaseUrl + "/") => new()
    {
        EnableAniListMetadata = enabled,
        AniListBaseUrl = baseUrl
    };

    private AniListMangaSeriesMetadataService CreateService(StubHandler handler, AppSettings? settings = null)
    {
        _factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        return Build(settings);
    }

    private AniListMangaSeriesMetadataService CreateServiceWhoseClientThrows(Exception exception, AppSettings? settings = null)
    {
        _factory.Setup(f => f.CreateClient(It.IsAny<string>())).Throws(exception);
        return Build(settings);
    }

    private AniListMangaSeriesMetadataService Build(AppSettings? settings)
    {
        var options = new Mock<IOptionsMonitor<AppSettings>>();
        options.Setup(o => o.CurrentValue).Returns(settings ?? Settings());
        return new AniListMangaSeriesMetadataService(_factory.Object, options.Object, _cache, _logger);
    }

    private void VerifyNoClientCreated()
        => _factory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never());

    [Fact]
    public void ProviderName_IsAniListManga()
    {
        Assert.Equal("AniListManga", CreateServiceWhoseClientThrows(new InvalidOperationException()).ProviderName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LookupAndSearch_BlankInput_ReturnNothingWithoutARequest(string input)
    {
        var service = CreateServiceWhoseClientThrows(new InvalidOperationException("no request expected"));

        Assert.Null(await service.LookupSeriesAsync(input));
        Assert.Empty(await service.SearchSeriesAsync(input));
        VerifyNoClientCreated();
    }

    [Fact]
    public async Task SearchSeriesAsync_Disabled_ReturnsEmptyWithoutARequest()
    {
        var service = CreateServiceWhoseClientThrows(new InvalidOperationException("no request expected"), Settings(enabled: false));

        Assert.Empty(await service.SearchSeriesAsync("Attack on Titan"));
        VerifyNoClientCreated();
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsTheCachedMatchForTheNormalizedName()
    {
        var cached = new ExternalSeriesMetadata { CanonicalTitle = "Attack on Titan", Source = "AniListManga" };
        _cache.Set("anilist-manga-series:attackontitan", cached);
        var service = CreateServiceWhoseClientThrows(new InvalidOperationException("no request expected"));

        var result = await service.LookupSeriesAsync("  ATTACK on titan! ");

        Assert.Same(cached, result);
        VerifyNoClientCreated();
    }

    [Fact]
    public async Task SearchSeriesAsync_ClientFailure_ReturnsEmptyLogsAndRecordsTheFailure()
    {
        var failure = new HttpRequestException("connection refused");
        var service = CreateServiceWhoseClientThrows(failure);

        Assert.Empty(await service.SearchSeriesAsync("Attack on Titan"));

        var warning = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal("AniList manga lookup failed for Attack on Titan", warning.Message);
        Assert.Same(failure, warning.Exception);
        _cache.Set(ProbeCacheKey, (true, "Reachable"));
        var health = await service.CheckHealthAsync();
        Assert.Equal(1, health.FailureCount);
        Assert.Equal("connection refused", health.LastError);
        Assert.True(health.Reachable);
        Assert.Equal("Reachable", health.StatusMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CheckHealthAsync_MissingBaseUrl_ReportsNotConfiguredWithoutProbing(string baseUrl)
    {
        var service = CreateServiceWhoseClientThrows(new InvalidOperationException("no request expected"), Settings(baseUrl: baseUrl));

        var health = await service.CheckHealthAsync();

        Assert.True(health.Enabled);
        Assert.False(health.Configured);
        Assert.Null(health.Reachable);
        Assert.Equal("Missing base URL", health.StatusMessage);
        VerifyNoClientCreated();
    }

    [Fact]
    public async Task CheckHealthAsync_CachedProbe_IsReportedWithoutProbing()
    {
        _cache.Set(ProbeCacheKey, (false, "HTTP 503 Service Unavailable"));
        var service = CreateServiceWhoseClientThrows(new InvalidOperationException("no request expected"));

        var health = await service.CheckHealthAsync();

        Assert.True(health.Configured);
        Assert.False(health.Reachable);
        Assert.Equal("HTTP 503 Service Unavailable", health.StatusMessage);
        VerifyNoClientCreated();
    }

    public static TheoryData<Exception, string> ProbeFailures => new()
    {
        { new TaskCanceledException("probe timed out"), "Timed out" },
        { new HttpRequestException("connection refused"), "Network error: connection refused" },
        { new InvalidOperationException("no handler"), "no handler" },
    };

    [Theory]
    [MemberData(nameof(ProbeFailures))]
    public async Task CheckHealthAsync_ProbeFailure_IsReportedAndCached(Exception failure, string expectedMessage)
    {
        var service = CreateServiceWhoseClientThrows(failure);

        var first = await service.CheckHealthAsync();
        var second = await service.CheckHealthAsync();

        Assert.False(first.Reachable);
        Assert.Equal(expectedMessage, first.StatusMessage);
        Assert.False(second.Reachable);
        Assert.Equal(expectedMessage, second.StatusMessage);
        _factory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Once());
    }

    [Fact]
    public async Task CheckHealthAsync_NullCachedProbe_IsIgnoredAndProbedAgain()
    {
        _cache.Set<object?>(ProbeCacheKey, null);
        var service = CreateServiceWhoseClientThrows(new HttpRequestException("connection refused"));

        var health = await service.CheckHealthAsync();

        Assert.False(health.Reachable);
        Assert.Equal("Network error: connection refused", health.StatusMessage);
        _factory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Once());
    }

    // Real request 1 of 2. A successful search is also what lets a recent
    // lookup override a failed probe, so both behaviours share this request.
    [Fact]
    public async Task SearchSeriesAsync_ParsesMediaDefensively_AndTheSuccessOverridesAFailedProbe()
    {
        const string json = """
        {"data":{"Page":{"media":[
          {"synonyms":[1,"  "]},
          {"title":"not an object","synonyms":["Synonym Only"],"coverImage":"not an object"},
          {"title":{"english":"No Cover","romaji":5},"description":42},
          {"title":{"romaji":"Blank Cover"},"coverImage":{"extraLarge":" ","large":null}}
        ]}}}
        """;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        var service = CreateService(handler);

        var results = await service.SearchSeriesAsync("Attack on Titan", limit: 500);

        Assert.Equal(new[] { "Synonym Only", "No Cover", "Blank Cover" }, results.Select(r => r.CanonicalTitle));
        Assert.All(results, r =>
        {
            Assert.Equal("AniListManga", r.Source);
            Assert.Empty(r.Aliases);
            Assert.Null(r.ImageUrl);
            Assert.Null(r.ThumbnailUrl);
            Assert.Null(r.Synopsis);
        });
        Assert.Equal(
            new (string, string?)[] { ("Synonym Only", null), ("No Cover", "en"), ("Blank Cover", "ja-Latn") },
            results.Select(r => (r.LocalizedTitles.Single().Title, r.LocalizedTitles.Single().Language)));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(BaseUrl, request.Uri);
        Assert.Equal("application/json", request.Accept);
        using (var body = JsonDocument.Parse(request.Body))
        {
            var variables = body.RootElement.GetProperty("variables");
            Assert.Equal("Attack on Titan", variables.GetProperty("search").GetString());
            Assert.Equal(50, variables.GetProperty("perPage").GetInt32());
            Assert.Contains("countryOfOrigin: \"JP\"", body.RootElement.GetProperty("query").GetString());
        }

        _cache.Set(ProbeCacheKey, (false, "HTTP 500 Internal Server Error"));
        var health = await service.CheckHealthAsync();

        Assert.Equal(1, health.SuccessCount);
        Assert.True(health.Reachable);
        Assert.Equal("Reachable (recent lookup succeeded)", health.StatusMessage);
    }

    // Real request 2 of 2. The test cancels its token as soon as the invoker
    // announces the back-off, so the production delay ends immediately.
    [Fact]
    public async Task CheckHealthAsync_DuringA429BackOffWindow_ReportsDegraded()
    {
        using var cts = new CancellationTokenSource();
        _logger.OnLog = entry =>
        {
            if (entry.Message.Contains(BackOffMessage, StringComparison.Ordinal))
            {
                cts.Cancel();
            }
        };
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return response;
        });
        var service = CreateService(handler);
        var before = DateTime.UtcNow;
        try
        {
            await service.SearchSeriesAsync("Attack on Titan", cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Either surfacing or swallowing the caller's cancellation is acceptable here.
        }
        _cache.Set(ProbeCacheKey, (true, "Reachable"));

        var health = await service.CheckHealthAsync();

        var backOff = Assert.Single(_logger.Entries, e => e.Message.Contains(BackOffMessage, StringComparison.Ordinal));
        Assert.Equal("AniListManga rate limit hit (HTTP 429); backing off for 120.0s before retry", backOff.Message);
        Assert.Single(handler.Requests);
        Assert.True(health.Reachable);
        Assert.True(health.RateLimited);
        Assert.Equal("Degraded - rate limited", health.StatusMessage);
        Assert.NotNull(health.RateLimitedUntilUtc);
        Assert.InRange(health.RateLimitedUntilUtc.Value, before.AddSeconds(119), DateTime.UtcNow.AddSeconds(121));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<(string Uri, string? Accept, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
            {
                Requests.Add((request.RequestUri!.OriginalString, request.Headers.Accept.ToString(), body));
            }
            return _respond(request);
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class ListLogger : ILogger<AniListMangaSeriesMetadataService>
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
            var entry = new LogEntry(logLevel, formatter(state, exception), exception);
            lock (_entries)
            {
                _entries.Add(entry);
            }
            OnLog?.Invoke(entry);
        }
    }
}
