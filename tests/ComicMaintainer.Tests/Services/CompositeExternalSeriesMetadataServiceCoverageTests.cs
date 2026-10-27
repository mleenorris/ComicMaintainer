using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class CompositeExternalSeriesMetadataServiceCoverageTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly ListLogger _logger = new();

    private CompositeExternalSeriesMetadataService CreateService(params IExternalSeriesMetadataService[] providers)
        => new(providers, _logger, _clock, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1));

    private static ExternalSeriesMetadata Meta(string title, string source = "Test")
        => new() { CanonicalTitle = title, Source = source };

    private static Mock<IExternalSeriesMetadataService> SearchProvider(params ExternalSeriesMetadata[] results)
    {
        var mock = new Mock<IExternalSeriesMetadataService>();
        mock.Setup(p => p.SearchSeriesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ExternalSeriesMetadata>)results);
        return mock;
    }

    private static Mock<IExternalSeriesMetadataService> FailingLookupProvider(Exception error)
    {
        var mock = new Mock<IExternalSeriesMetadataService>();
        mock.Setup(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(error);
        return mock;
    }

    [Fact]
    public void ProviderName_IsComposite_AndProvidersExposesTheConfiguredList()
    {
        var providers = new[]
        {
            Mock.Of<IExternalSeriesMetadataService>(),
            Mock.Of<IExternalSeriesMetadataService>()
        };

        var service = new CompositeExternalSeriesMetadataService(providers, _logger);

        Assert.Equal("Composite", service.ProviderName);
        Assert.Equal("Composite", ((IExternalSeriesMetadataService)service).ProviderName);
        Assert.Same(providers, service.Providers);
    }

    [Fact]
    public async Task LookupSeriesAsync_CallerCancellation_IsRethrownWithoutBackingOffTheProvider()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var provider = new Mock<IExternalSeriesMetadataService>();
        provider.Setup(p => p.LookupSeriesAsync("Batman", cts.Token))
            .ThrowsAsync(new OperationCanceledException(cts.Token));
        provider.Setup(p => p.LookupSeriesAsync("Batman", CancellationToken.None))
            .ReturnsAsync(Meta("Batman", "Primary"));
        var next = new Mock<IExternalSeriesMetadataService>();
        var service = CreateService(provider.Object, next.Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LookupSeriesAsync("Batman", cts.Token));

        next.Verify(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // Cancellation is not a provider failure: it is consulted again immediately.
        var result = await service.LookupSeriesAsync("Batman");
        Assert.Equal("Primary", result!.Source);
        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("cooling down"));
    }

    [Fact]
    public async Task LookupSeriesAsync_ProviderTimeoutWithoutCallerCancellation_IsTreatedAsFailure()
    {
        var timedOut = FailingLookupProvider(new TaskCanceledException("provider timeout"));
        var fallback = new Mock<IExternalSeriesMetadataService>();
        fallback.Setup(p => p.LookupSeriesAsync("Batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Meta("Batman", "Fallback"));
        var service = CreateService(timedOut.Object, fallback.Object);

        Assert.Equal("Fallback", (await service.LookupSeriesAsync("Batman"))!.Source);
        var failure = Assert.Single(_logger.Entries, e => e.Message.Contains("failed for Batman"));
        Assert.Equal(LogLevel.Debug, failure.Level);
        Assert.IsType<TaskCanceledException>(failure.Exception);

        Assert.Equal("Fallback", (await service.LookupSeriesAsync("Batman"))!.Source);
        timedOut.Verify(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(_logger.Entries, e => e.Message.Contains("cooling down until " + (Start + TimeSpan.FromSeconds(30)).ToString("O")));
    }

    [Fact]
    public async Task LookupSeriesAsync_BackoffIsCappedAtMaximum_EvenAfterManyFailures()
    {
        var failing = FailingLookupProvider(new HttpRequestException("boom"));
        var service = new CompositeExternalSeriesMetadataService(
            new[] { failing.Object }, _logger, _clock, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(45));

        await service.LookupSeriesAsync("Batman"); // failure 1 -> 30s
        _clock.Advance(TimeSpan.FromSeconds(30));
        await service.LookupSeriesAsync("Batman"); // failure 2 -> min(60s, 45s)
        _clock.Advance(TimeSpan.FromSeconds(44));
        await service.LookupSeriesAsync("Batman"); // still cooling down
        failing.Verify(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        // The exponent is capped, so dozens of consecutive failures neither
        // overflow nor exceed the configured maximum.
        for (var i = 0; i < 40; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(45));
            await service.LookupSeriesAsync("Batman");
        }
        failing.Verify(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(42));

        _clock.Advance(TimeSpan.FromSeconds(44));
        await service.LookupSeriesAsync("Batman");
        failing.Verify(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(42));
    }

    [Fact]
    public async Task SearchSeriesAsync_AggregatesProvidersInOrder_AndStopsOnceLimitIsReached()
    {
        var first = SearchProvider(Meta("A1"), Meta("A2"));
        var second = SearchProvider(Meta("B1"), Meta("B2"));
        var third = SearchProvider(Meta("C1"));
        var service = CreateService(first.Object, second.Object, third.Object);

        var results = await service.SearchSeriesAsync("query", 3);

        Assert.Equal(new[] { "A1", "A2", "B1" }, results.Select(r => r.CanonicalTitle));
        first.Verify(p => p.SearchSeriesAsync("query", 3, It.IsAny<CancellationToken>()), Times.Once);
        second.Verify(p => p.SearchSeriesAsync("query", 3, It.IsAny<CancellationToken>()), Times.Once);
        third.Verify(p => p.SearchSeriesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchSeriesAsync_ReturnsEverythingFound_WhenProvidersStayBelowLimit()
    {
        var first = SearchProvider(Meta("A1"));
        var empty = SearchProvider();
        var last = SearchProvider(Meta("B1"));
        var service = CreateService(first.Object, empty.Object, last.Object);

        var results = await service.SearchSeriesAsync("query");

        Assert.Equal(new[] { "A1", "B1" }, results.Select(r => r.CanonicalTitle));
        last.Verify(p => p.SearchSeriesAsync("query", 10, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchSeriesAsync_NoProviders_ReturnsEmpty()
    {
        var service = CreateService();

        Assert.Empty(await service.SearchSeriesAsync("query"));
    }

    [Fact]
    public async Task SearchSeriesAsync_FailingProvider_IsSkippedUntilItsCooldownExpires()
    {
        var failing = new Mock<IExternalSeriesMetadataService>();
        failing.Setup(p => p.SearchSeriesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"));
        var healthy = SearchProvider(Meta("H1"));
        var service = CreateService(failing.Object, healthy.Object);

        Assert.Equal("H1", Assert.Single(await service.SearchSeriesAsync("query")).CanonicalTitle);
        var failure = Assert.Single(_logger.Entries, e => e.Message.Contains("search failed for query"));
        Assert.Equal(LogLevel.Debug, failure.Level);
        Assert.IsType<HttpRequestException>(failure.Exception);
        Assert.EndsWith("next attempt at " + (Start + TimeSpan.FromSeconds(30)).ToString("O"), failure.Message);

        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal("H1", Assert.Single(await service.SearchSeriesAsync("query")).CanonicalTitle);
        failing.Verify(p => p.SearchSeriesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Debug
            && e.Message.Contains("search for query; cooling down until"));

        _clock.Advance(TimeSpan.FromSeconds(1));
        await service.SearchSeriesAsync("query");
        failing.Verify(p => p.SearchSeriesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SearchSeriesAsync_CallerCancellation_IsRethrown()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var provider = new Mock<IExternalSeriesMetadataService>();
        provider.Setup(p => p.SearchSeriesAsync("query", 10, cts.Token))
            .ThrowsAsync(new OperationCanceledException(cts.Token));
        var next = SearchProvider(Meta("N1"));
        var service = CreateService(provider.Object, next.Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchSeriesAsync("query", 10, cts.Token));

        next.Verify(p => p.SearchSeriesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task SearchSeriesAsync_SuccessfulSearch_ResetsBackoff()
    {
        var calls = 0;
        var flaky = new Mock<IExternalSeriesMetadataService>();
        flaky.Setup(p => p.SearchSeriesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++calls == 2
                ? Task.FromResult<IReadOnlyList<ExternalSeriesMetadata>>(new[] { Meta("F1") })
                : Task.FromException<IReadOnlyList<ExternalSeriesMetadata>>(new HttpRequestException("boom")));
        var service = CreateService(flaky.Object);

        Assert.Empty(await service.SearchSeriesAsync("query")); // failure -> 30s cooldown
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Single(await service.SearchSeriesAsync("query")); // success resets the failure count
        Assert.Empty(await service.SearchSeriesAsync("query")); // failure -> 30s again, not 60s
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(await service.SearchSeriesAsync("query"));

        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task CheckAllHealthAsync_ReportsProviderHealth_Backoff_AndProbeFailures()
    {
        var healthyStatus = new ProviderHealth
        {
            Name = "Healthy",
            Enabled = true,
            Configured = true,
            Reachable = true,
            StatusMessage = "OK"
        };
        var healthy = new Mock<IExternalSeriesMetadataService>();
        healthy.Setup(p => p.CheckHealthAsync(It.IsAny<CancellationToken>())).ReturnsAsync(healthyStatus);
        var coolingDown = FailingLookupProvider(new HttpRequestException("down"));
        coolingDown.SetupGet(p => p.ProviderName).Returns("Cooling");
        var broken = new Mock<IExternalSeriesMetadataService>();
        broken.SetupGet(p => p.ProviderName).Returns("Broken");
        broken.Setup(p => p.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("probe exploded"));
        var service = CreateService(healthy.Object, coolingDown.Object, broken.Object);
        Assert.Null(await service.LookupSeriesAsync("Batman"));
        _clock.Advance(TimeSpan.FromSeconds(10));

        var health = await service.CheckAllHealthAsync();

        Assert.Equal(3, health.Count);
        Assert.Same(healthyStatus, health[0]);

        Assert.Equal("Cooling", health[1].Name);
        Assert.True(health[1].Enabled);
        Assert.True(health[1].Configured);
        Assert.False(health[1].Reachable);
        Assert.Equal(
            "Backing off after recent failure; next attempt at " + (Start + TimeSpan.FromSeconds(30)).ToString("O"),
            health[1].StatusMessage);
        coolingDown.Verify(p => p.CheckHealthAsync(It.IsAny<CancellationToken>()), Times.Never);

        Assert.Equal("Broken", health[2].Name);
        Assert.False(health[2].Enabled);
        Assert.False(health[2].Configured);
        Assert.False(health[2].Reachable);
        Assert.Equal("probe exploded", health[2].StatusMessage);
        var probeLog = Assert.Single(_logger.Entries, e => e.Message.StartsWith("Health check failed for provider"));
        Assert.Equal(LogLevel.Debug, probeLog.Level);
        Assert.IsType<InvalidOperationException>(probeLog.Exception);
    }

    [Fact]
    public async Task CheckAllHealthAsync_NoProviders_ReturnsEmpty()
    {
        Assert.Empty(await CreateService().CheckAllHealthAsync());
    }

    private sealed class ListLogger : ILogger<CompositeExternalSeriesMetadataService>
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
