using System.Net;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public sealed class ComicVineSeriesMetadataServiceCoverageTests : IDisposable
{
    private const string BaseUrl = "https://comicvine.example/api";
    private const string ProbeCacheKey = "comicvine-health-probe";
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly ListLogger _logger = new();

    public void Dispose() => _cache.Dispose();

    private static AppSettings Settings(bool enabled = true, string? apiKey = "test-key") => new()
    {
        EnableExternalSeriesMetadata = enabled,
        ComicVineApiKey = apiKey,
        ComicVineBaseUrl = BaseUrl + "/"
    };

    private ComicVineSeriesMetadataService CreateService(StubHandler handler, AppSettings? settings = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        var options = new Mock<IOptionsMonitor<AppSettings>>();
        options.Setup(o => o.CurrentValue).Returns(settings ?? Settings());
        return new ComicVineSeriesMetadataService(factory.Object, options.Object, _cache, _logger);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    [Fact]
    public void ProviderName_IsComicVine()
    {
        Assert.Equal("ComicVine", CreateService(new StubHandler(_ => Json("{}"))).ProviderName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LookupAndSearch_BlankInput_ReturnNothingWithoutCallingTheApi(string input)
    {
        var handler = new StubHandler(_ => Json("{\"results\":[{\"name\":\"Batman\"}]}"));
        var service = CreateService(handler);

        Assert.Null(await service.LookupSeriesAsync(input));
        Assert.Empty(await service.SearchSeriesAsync(input));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false, "test-key")]
    [InlineData(true, null)]
    [InlineData(true, "  ")]
    public async Task LookupAndSearch_DisabledOrMissingApiKey_ReturnNothingWithoutCallingTheApi(bool enabled, string? apiKey)
    {
        var handler = new StubHandler(_ => Json("{\"results\":[{\"name\":\"Batman\"}]}"));
        var service = CreateService(handler, Settings(enabled, apiKey));

        Assert.Null(await service.LookupSeriesAsync("Batman"));
        Assert.Empty(await service.SearchSeriesAsync("Batman"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LookupSeriesAsync_CachesMatches_UnderTheNormalizedName()
    {
        var handler = new StubHandler(_ => Json("{\"results\":[{\"name\":\"Batman\"}]}"));
        var service = CreateService(handler);

        var first = await service.LookupSeriesAsync("Batman");
        var second = await service.LookupSeriesAsync("  BAT-MAN!! ");

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LookupSeriesAsync_NoMatch_IsNotCached()
    {
        var handler = new StubHandler(_ => Json("{\"results\":[]}"));
        var service = CreateService(handler);

        Assert.Null(await service.LookupSeriesAsync("Batman"));
        Assert.Null(await service.LookupSeriesAsync("Batman"));

        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(25, 25)]
    [InlineData(500, 50)]
    public async Task SearchSeriesAsync_ClampsLimit_AndReturnsEveryParsedResultInOrder(int requested, int expected)
    {
        var handler = new StubHandler(_ => Json("{\"results\":[{\"name\":\"Batman\"},{\"name\":\"Batman Beyond\"}]}"));
        var service = CreateService(handler);

        var results = await service.SearchSeriesAsync("Bat Man", requested);

        Assert.Equal(new[] { "Batman", "Batman Beyond" }, results.Select(r => r.CanonicalTitle));
        var uri = Assert.Single(handler.Requests);
        Assert.StartsWith(BaseUrl + "/search/?api_key=test-key&format=json&resources=volume&", uri.AbsoluteUri);
        Assert.Contains($"&limit={expected}&", uri.Query);
        Assert.EndsWith("&query=Bat%20Man", uri.Query);
    }

    [Fact]
    public async Task SearchSeriesAsync_ParsesOptionalFieldsDefensively()
    {
        const string json = """
        {
          "results": [
            { "aliases": "Nameless" },
            { "name": "   " },
            { "name": "No Optional Fields" },
            { "name": "Null Aliases", "aliases": null, "deck": null, "description": "<p>Only a description.</p>" },
            { "name": "Blank Aliases", "aliases": "  ", "deck": "   ", "description": "Description fallback" },
            { "name": "Non-string Text", "deck": 42, "description": 7 },
            { "name": "Dup Aliases", "aliases": "Dup Aliases\nOther\nother\n \n", "deck": "Deck wins", "description": "ignored" }
          ]
        }
        """;
        var service = CreateService(new StubHandler(_ => Json(json)));

        var results = await service.SearchSeriesAsync("anything");

        Assert.Equal(
            new[] { "No Optional Fields", "Null Aliases", "Blank Aliases", "Non-string Text", "Dup Aliases" },
            results.Select(r => r.CanonicalTitle));
        Assert.All(results, r => Assert.Equal("ComicVine", r.Source));

        Assert.Empty(results[0].Aliases);
        Assert.Null(results[0].Synopsis);
        Assert.Null(results[0].ImageUrl);
        Assert.Equal(new[] { "No Optional Fields" }, results[0].LocalizedTitles.Select(t => t.Title));

        Assert.Empty(results[1].Aliases);
        Assert.Equal("Only a description.", results[1].Synopsis);

        Assert.Empty(results[2].Aliases);
        Assert.Equal("Description fallback", results[2].Synopsis);

        Assert.Null(results[3].Synopsis);

        Assert.Equal(new[] { "Dup Aliases", "Other" }, results[4].Aliases);
        Assert.Equal("Deck wins", results[4].Synopsis);
        Assert.Equal(new[] { "Dup Aliases", "Other" }, results[4].LocalizedTitles.Select(t => t.Title));
    }

    [Theory]
    [InlineData("{\"error\":\"Invalid API Key\"}")]
    [InlineData("{\"results\":{\"name\":\"Batman\"}}")]
    public async Task LookupAndSearch_ResponseWithoutResultsArray_ReturnNothing(string json)
    {
        var service = CreateService(new StubHandler(_ => Json(json)));

        Assert.Null(await service.LookupSeriesAsync("Batman"));
        Assert.Empty(await service.SearchSeriesAsync("Batman"));
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task LookupSeriesAsync_MalformedJson_LogsWarningAndRecordsFailure()
    {
        var service = CreateService(new StubHandler(_ => Json("{ not json")));

        Assert.Null(await service.LookupSeriesAsync("Batman"));

        var warning = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal("ComicVine lookup failed for Batman", warning.Message);
        Assert.IsAssignableFrom<JsonException>(warning.Exception);
        _cache.Set(ProbeCacheKey, (true, "Reachable"));
        var health = await service.CheckHealthAsync();
        Assert.Equal(1, health.FailureCount);
        Assert.Equal(0, health.SuccessCount);
        Assert.Equal(warning.Exception!.Message, health.LastError);
    }

    [Fact]
    public async Task SearchSeriesAsync_TransportFailure_ReturnsEmptyAndLogsWarning()
    {
        var service = CreateService(new StubHandler(_ => throw new HttpRequestException("connection refused")));

        Assert.Empty(await service.SearchSeriesAsync("Batman"));

        var warning = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.IsType<HttpRequestException>(warning.Exception);
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
            ? () => service.LookupSeriesAsync("Batman", cts.Token)
            : () => service.SearchSeriesAsync("Batman", cancellationToken: cts.Token);

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

        Assert.Empty(await service.SearchSeriesAsync("Batman"));

        var warning = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal("ComicVine lookup failed for Batman", warning.Message);
        Assert.IsType<TaskCanceledException>(warning.Exception);
        _cache.Set(ProbeCacheKey, (true, "Reachable"));
        var health = await service.CheckHealthAsync();
        Assert.Equal(1, health.FailureCount);
        Assert.Equal(warning.Exception.Message, health.LastError);
    }

    [Fact]
    public async Task CheckHealthAsync_ReusesTheCachedProbeResult()
    {
        var handler = new StubHandler(_ => Json("{\"results\":[]}"));
        var service = CreateService(handler);

        var first = await service.CheckHealthAsync();
        var second = await service.CheckHealthAsync();

        Assert.True(first.Reachable);
        Assert.True(second.Reachable);
        Assert.Equal("Reachable", second.StatusMessage);
        var probe = Assert.Single(handler.Requests);
        Assert.Contains("&limit=1&", probe.Query);
        Assert.EndsWith("&query=ping", probe.Query);
    }

    [Fact]
    public async Task CheckHealthAsync_CachedUnreachableProbe_IsReportedWithoutProbing()
    {
        var handler = new StubHandler(_ => Json("{\"results\":[]}"));
        _cache.Set(ProbeCacheKey, (false, "HTTP 503 Service Unavailable"));
        var service = CreateService(handler);

        var health = await service.CheckHealthAsync();

        Assert.True(health.Enabled);
        Assert.True(health.Configured);
        Assert.False(health.Reachable);
        Assert.Equal("HTTP 503 Service Unavailable", health.StatusMessage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CheckHealthAsync_NullCachedProbe_IsIgnoredAndProbedAgain()
    {
        var handler = new StubHandler(_ => Json("{\"results\":[]}"));
        _cache.Set<object?>(ProbeCacheKey, null);
        var service = CreateService(handler);

        var health = await service.CheckHealthAsync();

        Assert.True(health.Reachable);
        Assert.Single(handler.Requests);
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

        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.RequestUri!);
            }
            return Task.FromResult(_respond(request));
        }
    }

    private sealed class ListLogger : ILogger<ComicVineSeriesMetadataService>
    {
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
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
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }
}
