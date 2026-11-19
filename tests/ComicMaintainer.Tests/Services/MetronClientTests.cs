using System.Net;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>Metron asked what a ComicVine volume is, without ever holding a ComicVine run up.</summary>
public sealed class MetronClientTests
{
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero));
    private Func<HttpRequestMessage, HttpResponseMessage> _answer = _ => Json(
        "{\"count\":1,\"results\":[{\"id\":42,\"series\":\"Batman (2016)\",\"volume\":3,\"year_began\":2016,\"issue_count\":158}]}");

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private MetronClient CreateClient(AppSettings? settings = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(MetronClient.HttpClientName)).Returns(() => new HttpClient(new Handler(this)));
        return new MetronClient(
            factory.Object,
            new TestOptionsMonitor<AppSettings>(settings ?? new AppSettings
            {
                EnableExternalSeriesMetadata = true,
                MetronUsername = "reader",
                MetronPassword = "secret",
                MetronBaseUrl = "https://metron.example/api/",
            }),
            NullLogger<MetronClient>.Instance,
            _time);
    }

    private sealed class Handler(MetronClientTests owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner._requests.Add(request);
            return Task.FromResult(owner._answer(request));
        }
    }

    [Fact]
    public async Task SeriesOfComicVineVolumeAsync_AsksByComicVineId_SignedIn_AndRemembersTheAnswer()
    {
        var client = CreateClient();

        var answer = await client.SeriesOfComicVineVolumeAsync(91273);

        Assert.True(answer.Available);
        Assert.Equal(new Core.Interfaces.MetronSeries(42, "Batman (2016)", 3, 2016), answer.Series);
        var request = Assert.Single(_requests);
        Assert.Equal("https://metron.example/api/series/?cv_id=91273", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal("reader:secret", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));

        // A week later it is asked again.
        Assert.Equal(answer, await client.SeriesOfComicVineVolumeAsync(91273));
        Assert.Single(_requests);
        _time.Advance(TimeSpan.FromDays(8));
        await client.SeriesOfComicVineVolumeAsync(91273);
        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task SeriesOfComicVineVolumeAsync_NoSeries_IsAnAnswer()
    {
        _answer = _ => Json("{\"count\":0,\"results\":[]}");
        var answer = await CreateClient().SeriesOfComicVineVolumeAsync(1);
        Assert.Equal((true, null), (answer.Available, answer.Series));

        _answer = _ => Json("{}", HttpStatusCode.NotFound);
        answer = await CreateClient().SeriesOfComicVineVolumeAsync(2);
        Assert.Equal((true, null), (answer.Available, answer.Series));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SeriesOfComicVineVolumeAsync_ARefusalOrFailure_PausesMetronForTenMinutes(HttpStatusCode status)
    {
        _answer = _ => Json("{}", status);
        var client = CreateClient();

        Assert.False((await client.SeriesOfComicVineVolumeAsync(1)).Available);
        Assert.False((await client.SeriesOfComicVineVolumeAsync(2)).Available);
        Assert.Single(_requests);

        _time.Advance(TimeSpan.FromMinutes(11));
        _answer = _ => Json("{\"results\":[{\"id\":7,\"name\":\"X\",\"volume\":1}]}");
        Assert.Equal(new Core.Interfaces.MetronSeries(7, "X", 1, null), (await client.SeriesOfComicVineVolumeAsync(2)).Series);
    }

    [Fact]
    public async Task SeriesOfComicVineVolumeAsync_AFailedRequestOrBadAnswer_IsUnavailable()
    {
        _answer = _ => throw new HttpRequestException("down");
        Assert.False((await CreateClient().SeriesOfComicVineVolumeAsync(1)).Available);

        _answer = _ => Json("not json");
        Assert.False((await CreateClient().SeriesOfComicVineVolumeAsync(1)).Available);
    }

    [Fact]
    public async Task SeriesOfComicVineVolumeAsync_StaysUnderEighteenAMinute()
    {
        var client = CreateClient();
        for (var id = 1; id <= MetronClient.RequestsPerMinute; id++)
        {
            Assert.True((await client.SeriesOfComicVineVolumeAsync(id)).Available);
        }

        Assert.False((await client.SeriesOfComicVineVolumeAsync(99)).Available);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await client.SeriesOfComicVineVolumeAsync(99)).Available);
        Assert.Equal(MetronClient.RequestsPerMinute + 1, _requests.Count);
    }

    [Theory]
    [InlineData(false, "reader", "secret")]
    [InlineData(true, "reader", null)]
    [InlineData(true, " ", "secret")]
    public async Task SeriesOfComicVineVolumeAsync_NotConfigured_AsksNothing(bool enabled, string? user, string? password)
    {
        var client = CreateClient(new AppSettings { EnableExternalSeriesMetadata = enabled, MetronUsername = user, MetronPassword = password });

        Assert.False(client.IsConfigured);
        Assert.False((await client.SeriesOfComicVineVolumeAsync(1)).Available);
        Assert.False((await CreateClient().SeriesOfComicVineVolumeAsync(0)).Available);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task SeriesOfComicVineVolumeAsync_Cancelled_Throws()
    {
        _answer = _ => throw new TaskCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateClient().SeriesOfComicVineVolumeAsync(1, new CancellationToken(true)));
    }
}
