using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Telemetry;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// <c>ProviderHealthTracker</c> is internal, so it is exercised through the MangaDex
/// provider, which feeds every request outcome into it.
/// </summary>
public class ProviderHealthTrackerTests
{
    [Fact]
    public async Task Snapshot_KeepsTheLastMeaningfulErrorWhenAFailureCarriesNoMessage()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return calls switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                2 => throw new HttpRequestException(string.Empty),
                _ => EmptyResults()
            };
        });
        var service = CreateService(handler);

        Assert.Null(await service.LookupSeriesAsync("Berserk"));
        Assert.Null(await service.LookupSeriesAsync("Vagabond"));
        var health = await service.CheckHealthAsync();

        Assert.Equal(2, health.FailureCount);
        Assert.Equal(0, health.SuccessCount);
        Assert.Equal("HTTP 500 Internal Server Error", health.LastError);
        Assert.NotNull(health.LastFailureUtc);
        Assert.False(health.RateLimited);
    }

    [Fact]
    public async Task PersistentRateLimit_CountsOnlyTheTerminal429AsAFailedRequest()
    {
        // Retry-After: 0 is clamped to the invoker's 250 ms floor before the single retry.
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.Query.Contains("title=ping", StringComparison.Ordinal))
            {
                return EmptyResults();
            }

            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "0");
            return response;
        });
        using var metrics = new ComicMaintainerMetrics();
        using var recorder = new Recorder(metrics.Meter);
        var service = CreateService(handler, metrics);

        Assert.Null(await service.LookupSeriesAsync("Berserk"));
        var measurements = recorder.Measurements;
        var health = await service.CheckHealthAsync();

        Assert.Equal(
            new[] { "failed" },
            measurements
                .Where(m => m.Instrument == "comicmaintainer.metadata_provider.requests")
                .Select(m => (string?)m.Tags["outcome"]));
        Assert.Equal(2, measurements.Count(m =>
            m.Instrument == "comicmaintainer.metadata_provider.rate_limit_waits"
            && (string?)m.Tags["kind"] == "server"));
        Assert.Equal(2, health.FailureCount);
        Assert.Equal("HTTP 429 Too Many Requests", health.LastError);
        Assert.True(health.RateLimited);
        Assert.True(health.Reachable);
        Assert.Equal("Degraded - rate limited", health.StatusMessage);
    }

    private static HttpResponseMessage EmptyResults() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{ "data": [] }""", Encoding.UTF8, "application/json")
    };

    private static MangaDexSeriesMetadataService CreateService(StubHandler handler, ComicMaintainerMetrics? metrics = null)
    {
        // The service disposes each client it creates, so hand out a fresh one per call.
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        var settings = new Mock<IOptionsMonitor<AppSettings>>();
        settings.Setup(s => s.CurrentValue).Returns(new AppSettings
        {
            EnableMangaDexMetadata = true,
            MangaDexBaseUrl = "https://api.mangadex.example"
        });

        return new MangaDexSeriesMetadataService(
            httpClientFactory.Object,
            settings.Object,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<MangaDexSeriesMetadataService>.Instance,
            metrics);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, Dictionary<string, object?> Tags)> _measurements = new();

        public Recorder(Meter meter)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, _, tags, _) => Add(i, tags));
            _listener.SetMeasurementEventCallback<double>((i, _, tags, _) => Add(i, tags));
            _listener.Start();
        }

        public IReadOnlyList<(string Instrument, Dictionary<string, object?> Tags)> Measurements
        {
            get
            {
                lock (_measurements)
                {
                    return _measurements.ToList();
                }
            }
        }

        private void Add(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                dict[tag.Key] = tag.Value;
            }

            lock (_measurements)
            {
                _measurements.Add((instrument.Name, dict));
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
