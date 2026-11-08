using System.Net;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// The application's own request pacing is not a provider failure: requests
/// wait out a used-up window, and a request that could not wait long enough
/// leaves the provider's health and back-off untouched. Both AniList providers
/// share one limiter, so each composite lookup costs two permits.
/// </summary>
public sealed class ProviderPacingTests : IDisposable
{
    private const string BaseUrl = "https://graphql.anilist.example";

    private readonly FakeTimeProvider _clock = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly ProviderRateLimiter _limiter;
    private int _requests;

    public ProviderPacingTests()
    {
        // The limiter AniList runs on by default: 28 permits a minute in twelve segments.
        _limiter = new ProviderRateLimiter(
            "AniList",
            new AppSettings().AniListRequestsPerMinute,
            TimeSpan.FromMinutes(1),
            segmentsPerWindow: 12,
            timeProvider: _clock);
    }

    public void Dispose()
    {
        _limiter.Dispose();
        _cache.Dispose();
    }

    private IOptionsMonitor<AppSettings> Options()
    {
        var options = new Mock<IOptionsMonitor<AppSettings>>();
        options.Setup(o => o.CurrentValue).Returns(new AppSettings { EnableAniListMetadata = true, AniListBaseUrl = BaseUrl });
        return options.Object;
    }

    private IHttpClientFactory Factory()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new StubHandler(this), disposeHandler: true));
        return factory.Object;
    }

    private AniListMangaSeriesMetadataService Manga(ProviderRateLimiter? limiter = null)
        => new(Factory(), Options(), _cache, NullLogger<AniListMangaSeriesMetadataService>.Instance, metrics: null, limiter ?? _limiter);

    private AniListManhwaSeriesMetadataService Manhwa(ProviderRateLimiter limiter)
        => new(Factory(), Options(), _cache, NullLogger<AniListManhwaSeriesMetadataService>.Instance, metrics: null, limiter);

    private CompositeExternalSeriesMetadataService Composite(params ComicMaintainer.Core.Interfaces.IExternalSeriesMetadataService[] providers)
        => new(
            providers,
            NullLogger<CompositeExternalSeriesMetadataService>.Instance,
            _clock,
            CompositeExternalSeriesMetadataService.InitialBackoff,
            CompositeExternalSeriesMetadataService.MaxBackoff);

    /// <summary>Waits until a request is queued for a permit (the clock is not moved).</summary>
    private async Task WaitUntilQueuedAsync()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (_limiter.QueuedPermits == 0)
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), "no request queued for a permit");
            await Task.Delay(1);
        }
    }

    [Fact]
    public async Task SequentialLookups_PastTheAniListPace_WaitForPermitsWithoutBackingOff()
    {
        // AniList's default limiter on a fiftieth of the clock: the same 28
        // permits in twelve segments, with a window of 1.2 s instead of a
        // minute. 28 permits cover 14 lookups; the 15th waits for the window.
        using var limiter = new ProviderRateLimiter(
            "AniList",
            new AppSettings().AniListRequestsPerMinute,
            TimeSpan.FromSeconds(1.2),
            segmentsPerWindow: 12);
        var manga = Manga(limiter);
        var manhwa = Manhwa(limiter);
        var composite = Composite(manga, manhwa);

        for (var index = 0; index < 20; index++)
        {
            var title = $"Series {index}";
            var match = await composite.LookupSeriesAsync(title);
            Assert.NotNull(match);
            Assert.Equal(title, match!.CanonicalTitle);
        }

        // One request from each provider per lookup: neither was skipped.
        Assert.Equal(40, Volatile.Read(ref _requests));

        var health = await manga.CheckHealthAsync();
        Assert.Equal(0, health.FailureCount);
        Assert.False(health.RateLimited);
    }

    [Fact]
    public void AniListDefaults_WaitLongEnoughForAWholeWindow_WithinTheLookupBudget()
    {
        // The default limiter's window is a minute; a used-up window must be
        // waited out rather than reported, and the composite must allow it.
        using var limiter = new ProviderRateLimiter(
            "AniList",
            new AppSettings().AniListRequestsPerMinute,
            TimeSpan.FromMinutes(1),
            segmentsPerWindow: 12);

        Assert.True(limiter.MaxPermitWait >= TimeSpan.FromMinutes(1));
        Assert.True(
            limiter.MaxPermitWait + TimeSpan.FromSeconds(ExternalSeriesMetadataProviderBase.MaxRequestTimeoutSeconds)
            <= CompositeExternalSeriesMetadataService.ProviderCallTimeout);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(15, 15)]
    [InlineData(30, 30)]
    [InlineData(120, 30)]
    public void RequestTimeout_IsClampedToTheProviderClientsOwnTimeout(int configured, int expectedSeconds)
    {
        var timeout = ExternalSeriesMetadataProviderBase.RequestTimeout(new AppSettings { MetadataProviderTimeoutSeconds = configured });

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), timeout);
    }

    [Fact]
    public async Task Search_WithNoRoomInThePace_IsPacedAndLeavesHealthAlone()
    {
        var manga = Manga();
        for (var index = 0; index < new AppSettings().AniListRequestsPerMinute; index++)
        {
            await manga.SearchSeriesAsync($"Series {index}");
        }

        var search = manga.SearchSeriesAsync("One more");
        await WaitUntilQueuedAsync();
        _clock.Advance(RateLimitedHttpInvoker.InteractivePermitWait);

        var failure = await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => search);
        Assert.Equal(ExternalMetadataFailureKind.Paced, failure.Kind);
        Assert.Null(failure.RetryAfter);
        Assert.Contains("pace limit", failure.Message);
        Assert.DoesNotContain("budget", failure.Message);

        var healthCheck = manga.CheckHealthAsync();
        await WaitUntilQueuedAsync();
        _clock.Advance(TimeSpan.FromSeconds(2));
        var health = await healthCheck;
        Assert.Equal(0, health.FailureCount);
        Assert.False(health.RateLimited);
        Assert.NotEqual(false, health.Reachable);
        Assert.DoesNotContain("Degraded", health.StatusMessage);
    }

    [Fact]
    public async Task ForgetNoMatch_MakesTheNextLookupAskTheProviderAgain()
    {
        var manga = Manga();
        const string title = "Nothing Like This";

        Assert.Null(await manga.LookupSeriesAsync(title + " (no match)"));
        var afterFirstMiss = Volatile.Read(ref _requests);
        Assert.Null(await manga.LookupSeriesAsync(title + " (no match)"));
        Assert.Equal(afterFirstMiss, Volatile.Read(ref _requests));

        manga.ForgetNoMatch(title + " (no match)");
        Assert.Null(await manga.LookupSeriesAsync(title + " (no match)"));

        Assert.Equal(afterFirstMiss + 1, Volatile.Read(ref _requests));
    }

    private sealed class StubHandler(ProviderPacingTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref owner._requests);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            // Answer with the searched title, so every lookup has an exact match,
            // except for the title that is meant to miss.
            var match = System.Text.RegularExpressions.Regex.Match(body, "\"search\":\"([^\"]*)\"");
            var title = match.Success ? match.Groups[1].Value : "Unrelated";
            var media = title.Contains("no match", StringComparison.Ordinal)
                ? string.Empty
                : "{\"id\":1,\"title\":{\"english\":\"" + title + "\"}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":{\"Page\":{\"media\":[" + media + "]}}}", Encoding.UTF8, "application/json")
            };
        }
    }
}
