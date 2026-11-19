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

    private TestOptionsMonitor<AppSettings> _options = null!;

    private static AppSettings Account(string password = "secret", string baseUrl = "https://metron.example/api/") => new()
    {
        EnableExternalSeriesMetadata = true,
        MetronUsername = "reader",
        MetronPassword = password,
        MetronBaseUrl = baseUrl,
    };

    private MetronClient CreateClient(AppSettings? settings = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(MetronClient.HttpClientName)).Returns(() => new HttpClient(new Handler(this)));
        _options = new TestOptionsMonitor<AppSettings>(settings ?? Account());
        return new MetronClient(factory.Object, _options, NullLogger<MetronClient>.Instance, _time);
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
    public async Task SeriesOfComicVineVolumeAsync_AnApiToken_IsSentAsABearerToken_InsteadOfThePassword()
    {
        var client = CreateClient(new AppSettings
        {
            EnableExternalSeriesMetadata = true,
            MetronApiToken = " knox-token ",
            MetronUsername = "reader",
            MetronPassword = "secret",
            MetronBaseUrl = "https://metron.example/api",
        });

        Assert.True(client.IsConfigured);
        Assert.True((await client.SeriesOfComicVineVolumeAsync(91273)).Available);

        var request = Assert.Single(_requests);
        Assert.Equal(("Bearer", "knox-token"), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));

        // A token alone is enough.
        Assert.True(CreateClient(new AppSettings { EnableExternalSeriesMetadata = true, MetronApiToken = "t" }).IsConfigured);
    }

    [Fact]
    public async Task SeriesOfComicVineVolumeAsync_ARefusedToken_SaysSo()
    {
        _answer = _ => Json("{}", HttpStatusCode.Unauthorized);
        var client = CreateClient(new AppSettings { EnableExternalSeriesMetadata = true, MetronApiToken = "old", MetronBaseUrl = "https://metron.example/api" });

        await client.SeriesOfComicVineVolumeAsync(1);

        Assert.Equal("Metron refused the API token: check it under Settings", client.Problem);
    }

    [Theory]
    [InlineData(false, "secret", "Disabled in settings")]
    [InlineData(true, null, "Missing API token (or username and password)")]
    public async Task CheckHealthAsync_NotSetUp_SaysWhy_AndAsksNothing(bool enabled, string? password, string message)
    {
        var client = CreateClient(new AppSettings { EnableExternalSeriesMetadata = enabled, MetronUsername = "reader", MetronPassword = password });

        var health = await client.CheckHealthAsync();

        Assert.Equal(("Metron", enabled, false, (bool?)null, message), (health.Name, health.Enabled, health.Configured, health.Reachable, health.StatusMessage));
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task CheckHealthAsync_ProbesAtMostEveryTenMinutes_AndCountsTheRequests()
    {
        var client = CreateClient();

        var health = await client.CheckHealthAsync();

        Assert.Equal((true, true, (bool?)true, "Reachable", 1L, 0L), (health.Enabled, health.Configured, health.Reachable, health.StatusMessage, health.SuccessCount, health.FailureCount));
        Assert.Equal($"https://metron.example/api/series/?cv_id={MetronClient.ProbeVolumeId}", Assert.Single(_requests).RequestUri!.AbsoluteUri);
        Assert.NotNull(health.LastSuccessUtc);

        Assert.True((await client.CheckHealthAsync()).Reachable);
        Assert.Single(_requests);

        // Ten minutes on, Metron fails: the screen says how.
        _time.Advance(TimeSpan.FromMinutes(11));
        _answer = _ => Json("{}", HttpStatusCode.ServiceUnavailable);
        health = await client.CheckHealthAsync();
        Assert.Equal(((bool?)false, "Metron answered 503", "Metron answered 503", 1L, 1L),
            (health.Reachable, health.StatusMessage, health.LastError, health.SuccessCount, health.FailureCount));
        Assert.NotNull(health.LastFailureUtc);
    }

    [Fact]
    public async Task CheckHealthAsync_ARefusedSignIn_ShowsAsNotReachable_WithoutAskingAgain()
    {
        _answer = _ => Json("{}", HttpStatusCode.Unauthorized);
        var client = CreateClient();
        await client.SeriesOfComicVineVolumeAsync(1);
        _time.Advance(TimeSpan.FromMinutes(11));

        var health = await client.CheckHealthAsync();

        Assert.Equal(((bool?)false, "Metron refused the username and password: check them under Settings"), (health.Reachable, health.StatusMessage));
        Assert.Single(_requests);
    }

    [Fact]
    public async Task SeriesOfComicVineVolumeAsync_NoSeries_IsAnAnswer_ButA404IsAWrongAddress()
    {
        _answer = _ => Json("{\"count\":0,\"results\":[]}");
        var client = CreateClient();
        var answer = await client.SeriesOfComicVineVolumeAsync(1);
        Assert.Equal((true, null), (answer.Available, answer.Series));
        Assert.Null(client.Problem);

        _answer = _ => Json("{}", HttpStatusCode.NotFound);
        client = CreateClient();
        answer = await client.SeriesOfComicVineVolumeAsync(2);
        Assert.False(answer.Available);
        Assert.Equal("Metron answered 404: check the Metron address", client.Problem);
    }

    [Fact]
    public async Task SeriesOfComicVineVolumeAsync_ARefusedSignIn_IsTriedAgainAfterSixHours()
    {
        _answer = _ => Json("{}", HttpStatusCode.Unauthorized);
        var client = CreateClient();
        await client.SeriesOfComicVineVolumeAsync(1);

        _time.Advance(TimeSpan.FromHours(5));
        await client.SeriesOfComicVineVolumeAsync(1);
        Assert.Single(_requests);
        _time.Advance(TimeSpan.FromHours(2));
        await client.SeriesOfComicVineVolumeAsync(1);
        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task SeriesOfComicVineVolumeAsync_AForbiddenAnswer_OnlyPauses()
    {
        _answer = _ => Json("{}", HttpStatusCode.Forbidden);
        var client = CreateClient();
        await client.SeriesOfComicVineVolumeAsync(1);

        Assert.Equal("Metron answered 403", client.Problem);
        _time.Advance(TimeSpan.FromMinutes(11));
        await client.SeriesOfComicVineVolumeAsync(1);
        Assert.Equal(2, _requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task SeriesOfComicVineVolumeAsync_ARefusedSignIn_StopsMetronUntilTheAccountChanges(HttpStatusCode status)
    {
        _answer = _ => Json("{}", status);
        var client = CreateClient();

        Assert.False((await client.SeriesOfComicVineVolumeAsync(1)).Available);
        Assert.Equal("Metron refused the username and password: check them under Settings", client.Problem);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.False((await client.SeriesOfComicVineVolumeAsync(1)).Available);
        Assert.Single(_requests);

        // A new password is tried, and the problem it had is not shown for it.
        _options.Set(Account(password: "better"));
        Assert.Null(client.Problem);
        _answer = _ => Json("{\"results\":[{\"id\":7,\"volume\":1}]}");
        Assert.True((await client.SeriesOfComicVineVolumeAsync(1)).Available);
        Assert.Equal(2, _requests.Count);
    }

    [Theory]
    [InlineData("http://metron.example/api")]
    [InlineData("metron.example/api")]
    [InlineData("https://[bad")]
    public async Task SeriesOfComicVineVolumeAsync_AnAddressThatIsNotHttps_SendsNoPassword(string baseUrl)
    {
        var client = CreateClient(Account(baseUrl: baseUrl));

        Assert.False((await client.SeriesOfComicVineVolumeAsync(1)).Available);
        Assert.Empty(_requests);
        Assert.Equal("The Metron address must be an https address, such as https://metron.cloud/api", client.Problem);
    }

    [Fact]
    public async Task SeriesOfComicVineVolumeAsync_AnotherAccountOrAddress_AsksAgain()
    {
        var client = CreateClient();
        await client.SeriesOfComicVineVolumeAsync(1);
        _options.Set(Account(baseUrl: "https://other.example/api"));

        await client.SeriesOfComicVineVolumeAsync(1);

        Assert.Equal(["https://metron.example/api/series/?cv_id=1", "https://other.example/api/series/?cv_id=1"], _requests.Select(request => request.RequestUri!.AbsoluteUri));
    }

    [Fact]
    public async Task SeriesOfComicVineVolumeAsync_ABackgroundRun_WaitsForTheNextPermit()
    {
        var client = CreateClient();
        for (var id = 1; id <= MetronClient.RequestsPerMinute; id++)
        {
            await client.SeriesOfComicVineVolumeAsync(id);
        }

        var waiting = client.SeriesOfComicVineVolumeAsync(99, waitForPermit: true);
        Assert.False(waiting.IsCompleted);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await waiting).Available);
        Assert.Equal(MetronClient.RequestsPerMinute + 1, _requests.Count);
    }

    [Fact]
    public async Task SeriesOfComicVineVolumeAsync_StopsForTheDayBeforeMetronsDailyLimit()
    {
        var client = CreateClient();
        for (var id = 1; id <= MetronClient.RequestsPerDay; id++)
        {
            if ((id - 1) % MetronClient.RequestsPerMinute == 0 && id > 1)
            {
                _time.Advance(TimeSpan.FromMinutes(1));
            }

            await client.SeriesOfComicVineVolumeAsync(id);
        }

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.False((await client.SeriesOfComicVineVolumeAsync(999999)).Available);
        _time.Advance(TimeSpan.FromDays(1));
        Assert.True((await client.SeriesOfComicVineVolumeAsync(999999)).Available);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
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

        _answer = _ => throw new InvalidOperationException("anything else");
        var client = CreateClient();
        Assert.False((await client.SeriesOfComicVineVolumeAsync(1)).Available);
        Assert.Equal("anything else", client.Problem);
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateClient().SeriesOfComicVineVolumeAsync(1, false, new CancellationToken(true)));
    }
}
