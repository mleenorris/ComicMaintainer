using System.Net;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Notifications;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace ComicMaintainer.Tests.Notifications;

/// <summary>
/// Each channel against the contract its target documents, with a fake HTTP handler: the URL,
/// the method, the authentication and the body. See the notes in NotificationChannels.cs for
/// which document each one follows.
/// </summary>
public sealed class NotificationChannelTests
{
    private static readonly NotificationMessage Chapters = new(
        NotificationEventType.NewChapters,
        "3 new chapters: Berserk",
        "1, 2, 3",
        false,
        "Berserk",
        3,
        new[] { "1", "2", "3" },
        new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc));

    private static readonly NotificationMessage Failure = Chapters with { IsFailure = true, EventType = NotificationEventType.JobFailed };

    private static (StubHttpHandler Handler, NotificationHttpClient Http) Http(HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHttpHandler(_ => NotificationTestHttp.Response(status));
        return (handler, NotificationTestHttp.ClientOver(handler));
    }

    private static JsonElement Body(StubHttpHandler.CapturedRequest request)
    {
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }

    // ntfy -----------------------------------------------------------------

    [Fact]
    public async Task Ntfy_PostsJsonToTheRootUrlWithTheToken()
    {
        var (handler, http) = Http();
        var settings = new AppSettings { NotifyNtfyUrl = "https://ntfy.example/sub", NotifyNtfyTopic = "comics", NotifyNtfyToken = "tk_abc" };
        var channel = new NtfyNotificationChannel(http, NotificationTestHttp.Options(settings));

        await channel.SendAsync(Chapters, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://ntfy.example/sub", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer tk_abc", request.Headers["Authorization"]);
        var body = Body(request);
        Assert.Equal("comics", body.GetProperty("topic").GetString());
        Assert.Equal("3 new chapters: Berserk", body.GetProperty("title").GetString());
        Assert.Equal("1, 2, 3", body.GetProperty("message").GetString());
        Assert.Equal(3, body.GetProperty("priority").GetInt32());
    }

    [Fact]
    public async Task Ntfy_SendsNoAuthorizationWithoutAToken_AndRaisesFailuresPriority()
    {
        var (handler, http) = Http();
        var settings = new AppSettings { NotifyNtfyUrl = "https://ntfy.sh", NotifyNtfyTopic = "t" };
        var channel = new NtfyNotificationChannel(http, NotificationTestHttp.Options(settings));

        await channel.SendAsync(Failure, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.False(request.Headers.ContainsKey("Authorization"));
        Assert.Equal(4, Body(request).GetProperty("priority").GetInt32());
    }

    [Fact]
    public async Task Ntfy_KeepsTheMessageWithinTheSizeThatStaysAMessage()
    {
        var (handler, http) = Http();
        var settings = new AppSettings { NotifyNtfyUrl = "https://ntfy.sh", NotifyNtfyTopic = "t" };
        var channel = new NtfyNotificationChannel(http, NotificationTestHttp.Options(settings));

        await channel.SendAsync(Chapters with { Body = new string('é', 3000) }, CancellationToken.None);

        var message = Body(Assert.Single(handler.Requests)).GetProperty("message").GetString()!;
        Assert.True(Encoding.UTF8.GetByteCount(message) <= 4096);
        Assert.EndsWith("…", message);
    }

    [Fact]
    public async Task Ntfy_RefusesWhenItHasNoTopic()
    {
        var (_, http) = Http();
        var channel = new NtfyNotificationChannel(http, NotificationTestHttp.Options(new AppSettings { NotifyNtfyUrl = "https://ntfy.sh" }));

        await Assert.ThrowsAsync<NotificationNotConfiguredException>(() => channel.SendAsync(Chapters, CancellationToken.None));
    }

    // Gotify ---------------------------------------------------------------

    [Fact]
    public async Task Gotify_PostsJsonToMessageWithTheAppToken()
    {
        var (handler, http) = Http();
        var settings = new AppSettings { NotifyGotifyUrl = "https://gotify.example/", NotifyGotifyToken = "Axxx" };
        var channel = new GotifyNotificationChannel(http, NotificationTestHttp.Options(settings));

        await channel.SendAsync(Failure, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://gotify.example/message", request.Uri.AbsoluteUri);
        Assert.Equal("Axxx", request.Headers["X-Gotify-Key"]);
        var body = Body(request);
        Assert.Equal("1, 2, 3", body.GetProperty("message").GetString());
        Assert.Equal(7, body.GetProperty("priority").GetInt32());
    }

    [Fact]
    public async Task Gotify_RefusesWithoutAToken()
    {
        var (_, http) = Http();
        var channel = new GotifyNotificationChannel(http, NotificationTestHttp.Options(new AppSettings { NotifyGotifyUrl = "https://g.example" }));

        await Assert.ThrowsAsync<NotificationNotConfiguredException>(() => channel.SendAsync(Chapters, CancellationToken.None));
    }

    // Discord --------------------------------------------------------------

    [Fact]
    public async Task Discord_PostsContentThatCannotPingAnyone()
    {
        var (handler, http) = Http(HttpStatusCode.NoContent);
        var settings = new AppSettings { NotifyDiscordWebhookUrl = "https://discord.com/api/webhooks/1/token" };
        var channel = new DiscordNotificationChannel(http, NotificationTestHttp.Options(settings));

        await channel.SendAsync(Chapters with { Title = "@everyone rejoice" }, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://discord.com/api/webhooks/1/token", request.Uri.AbsoluteUri);
        var body = Body(request);
        Assert.Contains("@everyone rejoice", body.GetProperty("content").GetString());
        Assert.Empty(body.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
        Assert.Equal("ComicMaintainer", body.GetProperty("username").GetString());
    }

    [Fact]
    public async Task Discord_KeepsContentWithinItsLimit()
    {
        var (handler, http) = Http();
        var settings = new AppSettings { NotifyDiscordWebhookUrl = "https://discord.com/api/webhooks/1/token" };
        var channel = new DiscordNotificationChannel(http, NotificationTestHttp.Options(settings));

        await channel.SendAsync(Chapters with { Body = new string('x', 5000) }, CancellationToken.None);

        Assert.True(Body(Assert.Single(handler.Requests)).GetProperty("content").GetString()!.Length <= 2000);
    }

    [Fact]
    public async Task Discord_NeverCutsACharacterInHalf()
    {
        var (handler, http) = Http();
        var settings = new AppSettings { NotifyDiscordWebhookUrl = "https://discord.com/api/webhooks/1/token" };
        var channel = new DiscordNotificationChannel(http, NotificationTestHttp.Options(settings));

        // "**T**\n" is six characters, so the cut falls in the middle of an emoji.
        await channel.SendAsync(Chapters with { Title = "T", Body = string.Concat(Enumerable.Repeat("😀", 1500)) }, CancellationToken.None);

        var content = Body(Assert.Single(handler.Requests)).GetProperty("content").GetString()!;
        Assert.DoesNotContain('�', content);
        Assert.True(content.Length <= 2000);
    }

    [Fact]
    public async Task Discord_MasksItsUrlInTheTargetsError()
    {
        const string url = "https://discord.com/api/webhooks/1/super-secret-token";
        var handler = new StubHttpHandler(_ => NotificationTestHttp.Response(HttpStatusCode.Forbidden, $"bad url {url}"));
        var channel = new DiscordNotificationChannel(
            NotificationTestHttp.ClientOver(handler),
            NotificationTestHttp.Options(new AppSettings { NotifyDiscordWebhookUrl = url }));

        var failure = await Assert.ThrowsAsync<NotificationDeliveryException>(() => channel.SendAsync(Chapters, CancellationToken.None));

        Assert.DoesNotContain("super-secret-token", failure.Message);
        Assert.False(failure.IsTransient);
    }

    // Webhook --------------------------------------------------------------

    [Fact]
    public async Task Webhook_PostsTheEventAsJson()
    {
        var (handler, http) = Http();
        var settings = new AppSettings { NotifyWebhookUrl = "https://hook.example/abc?token=1" };
        var channel = new WebhookNotificationChannel(http, NotificationTestHttp.Options(settings));

        await channel.SendAsync(Chapters, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://hook.example/abc?token=1", request.Uri.AbsoluteUri);
        var body = Body(request);
        Assert.Equal(NotificationEventType.NewChapters, body.GetProperty("event").GetString());
        Assert.Equal("Berserk", body.GetProperty("series").GetString());
        Assert.Equal(3, body.GetProperty("count").GetInt32());
        Assert.Equal(3, body.GetProperty("items").GetArrayLength());
        Assert.False(body.GetProperty("isFailure").GetBoolean());
        Assert.Equal("ComicMaintainer", body.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Webhook_RefusesWithoutAUrl()
    {
        var (_, http) = Http();
        var channel = new WebhookNotificationChannel(http, NotificationTestHttp.Options(new AppSettings()));

        await Assert.ThrowsAsync<NotificationNotConfiguredException>(() => channel.SendAsync(Chapters, CancellationToken.None));
    }

    [Fact]
    public async Task Channel_RefusesAStoredUrlThatIsNotHttp()
    {
        var (_, http) = Http();
        var channel = new WebhookNotificationChannel(http, NotificationTestHttp.Options(new AppSettings { NotifyWebhookUrl = "ftp://hook.example/" }));

        await Assert.ThrowsAsync<NotificationNotConfiguredException>(() => channel.SendAsync(Chapters, CancellationToken.None));
    }

    // Komga ----------------------------------------------------------------

    [Fact]
    public async Task Komga_ScansTheConfiguredLibraryWithTheApiKey()
    {
        var (handler, http) = Http(HttpStatusCode.Accepted);
        var settings = new AppSettings { NotifyKomgaUrl = "http://komga.lan:25600", NotifyKomgaApiKey = "key", NotifyKomgaLibraryId = "0BQ7W" };
        var channel = new KomgaScanChannel(http, NotificationTestHttp.Options(settings));

        await channel.SendAsync(Chapters, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://komga.lan:25600/api/v1/libraries/0BQ7W/scan", request.Uri.AbsoluteUri);
        Assert.Equal("key", request.Headers["X-API-Key"]);
    }

    [Fact]
    public async Task Komga_ScansEveryLibraryWhenNoneIsConfigured()
    {
        var handler = new StubHttpHandler(request => request.Method == HttpMethod.Get
            ? NotificationTestHttp.Response(HttpStatusCode.OK, """[{"id":"LIB1","name":"Manga"},{"id":"LIB2","name":"Comics"}]""")
            : NotificationTestHttp.Response(HttpStatusCode.Accepted));
        var settings = new AppSettings { NotifyKomgaUrl = "http://komga.lan", NotifyKomgaApiKey = "key" };
        var channel = new KomgaScanChannel(NotificationTestHttp.ClientOver(handler), NotificationTestHttp.Options(settings));

        await channel.SendAsync(Chapters, CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("http://komga.lan/api/v1/libraries", handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal("key", handler.Requests[0].Headers["X-API-Key"]);
        Assert.Equal("http://komga.lan/api/v1/libraries/LIB1/scan", handler.Requests[1].Uri.AbsoluteUri);
        Assert.Equal("http://komga.lan/api/v1/libraries/LIB2/scan", handler.Requests[2].Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("[]", "no libraries")]
    [InlineData("{}", "no libraries")]
    [InlineData("""[{"name":"no id"}]""", "no libraries")]
    [InlineData("<html>sign in</html>", "could not be read")]
    [InlineData("[1,2]", "could not be read")]
    [InlineData("""[{"id":5}]""", "could not be read")]
    public async Task Komga_FailsPermanently_WhenTheLibraryListIsUnusable(string list, string reason)
    {
        var handler = new StubHttpHandler(_ => NotificationTestHttp.Response(HttpStatusCode.OK, list));
        var settings = new AppSettings { NotifyKomgaUrl = "http://komga.lan", NotifyKomgaApiKey = "key" };
        var channel = new KomgaScanChannel(NotificationTestHttp.ClientOver(handler), NotificationTestHttp.Options(settings));

        var failure = await Assert.ThrowsAsync<NotificationDeliveryException>(() => channel.SendAsync(Chapters, CancellationToken.None));

        Assert.False(failure.IsTransient);
        Assert.Contains(reason, failure.Message);
    }

    [Fact]
    public async Task Komga_RefusesWithoutAnApiKey()
    {
        var (_, http) = Http();
        var channel = new KomgaScanChannel(http, NotificationTestHttp.Options(new AppSettings { NotifyKomgaUrl = "http://komga.lan" }));

        await Assert.ThrowsAsync<NotificationNotConfiguredException>(() => channel.SendAsync(Chapters, CancellationToken.None));
    }

    [Fact]
    public async Task Komga_MasksTheApiKeyInItsError()
    {
        var handler = new StubHttpHandler(_ => NotificationTestHttp.Response(HttpStatusCode.Forbidden, "key secret-key was refused"));
        var settings = new AppSettings { NotifyKomgaUrl = "http://komga.lan", NotifyKomgaApiKey = "secret-key", NotifyKomgaLibraryId = "L" };
        var channel = new KomgaScanChannel(NotificationTestHttp.ClientOver(handler), NotificationTestHttp.Options(settings));

        var failure = await Assert.ThrowsAsync<NotificationDeliveryException>(() => channel.SendAsync(Chapters, CancellationToken.None));

        Assert.DoesNotContain("secret-key", failure.Message);
    }

    // Kavita ---------------------------------------------------------------

    [Fact]
    public async Task Kavita_ScansAllLibrariesWithTheAuthKey()
    {
        var (handler, http) = Http();
        var settings = new AppSettings { NotifyKavitaUrl = "http://kavita.lan:5000/", NotifyKavitaApiKey = "auth-key" };
        var channel = new KavitaScanChannel(http, NotificationTestHttp.Options(settings));

        await channel.SendAsync(Chapters, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://kavita.lan:5000/api/Library/scan-all", request.Uri.AbsoluteUri);
        Assert.Equal("auth-key", request.Headers["x-api-key"]);
    }

    [Fact]
    public async Task Kavita_ScansOneLibraryWhenConfigured()
    {
        var (handler, http) = Http();
        var settings = new AppSettings { NotifyKavitaUrl = "http://kavita.lan", NotifyKavitaApiKey = "k", NotifyKavitaLibraryId = "4" };
        var channel = new KavitaScanChannel(http, NotificationTestHttp.Options(settings));

        await channel.SendAsync(Chapters, CancellationToken.None);

        Assert.Equal("http://kavita.lan/api/Library/scan?libraryId=4", Assert.Single(handler.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Kavita_RefusesWithoutAnApiKey()
    {
        var (_, http) = Http();
        var channel = new KavitaScanChannel(http, NotificationTestHttp.Options(new AppSettings { NotifyKavitaUrl = "http://k.lan" }));

        await Assert.ThrowsAsync<NotificationNotConfiguredException>(() => channel.SendAsync(Chapters, CancellationToken.None));
    }

    // The HTTP rules every channel shares -----------------------------------

    private static HttpRequestMessage Request() =>
        NotificationHttpClient.CreateRequest(HttpMethod.Post, new Uri("https://target.example/hook"));

    [Fact]
    public async Task Http_Success_ReturnsTheBody()
    {
        var handler = new StubHttpHandler(_ => NotificationTestHttp.Response(HttpStatusCode.OK, "ok"));
        var factory = new StubHttpClientFactory(handler);

        Assert.Equal("ok", await new NotificationHttpClient(factory).SendAsync(Request(), "target", [], CancellationToken.None));
        Assert.Equal(NotificationHttpClient.HttpClientName, factory.RequestedName);
    }

    [Fact]
    public void Http_CreateRequest_RefusesOtherSchemes()
    {
        Assert.Throws<NotificationNotConfiguredException>(() =>
            NotificationHttpClient.CreateRequest(HttpMethod.Post, new Uri("ftp://target.example/")));
    }

    [Fact]
    public void Http_CreateRequest_MarksTheRequestSoItIsLeftOutOfTraces()
    {
        Assert.True(NotificationHttpClient.IsNotificationRequest(Request()));
        Assert.False(NotificationHttpClient.IsNotificationRequest(new HttpRequestMessage(HttpMethod.Get, "https://x.example/")));
    }

    [Fact]
    public async Task Http_TimesOutInsteadOfWaitingForever()
    {
        var handler = new StubHttpHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return NotificationTestHttp.Response(HttpStatusCode.OK);
        });
        var http = NotificationTestHttp.ClientOver(handler);

        var failure = await Assert.ThrowsAsync<NotificationDeliveryException>(() => http.SendAsync(Request(), "target", [], CancellationToken.None));

        Assert.True(failure.IsTransient);
        Assert.Contains("did not answer", failure.Message);
    }

    [Fact]
    public async Task Http_CancellationByTheCaller_IsNotReportedAsAFailure()
    {
        var handler = new StubHttpHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return NotificationTestHttp.Response(HttpStatusCode.OK);
        });
        var http = NotificationTestHttp.ClientOver(handler);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.SendAsync(Request(), "target", [], cancellation.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.Moved)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    public async Task Http_DoesNotFollowARedirect(HttpStatusCode status)
    {
        var handler = new StubHttpHandler(request =>
        {
            var response = NotificationTestHttp.Response(status);
            response.Headers.Location = new Uri("https://attacker.example/");
            return response;
        });
        var http = NotificationTestHttp.ClientOver(handler);

        var failure = await Assert.ThrowsAsync<NotificationDeliveryException>(() => http.SendAsync(Request(), "target", [], CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.False(failure.IsTransient);
        Assert.Contains("redirect", failure.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public async Task Http_TreatsServerAndRateLimitErrorsAsTemporary(HttpStatusCode status, bool transient)
    {
        var http = NotificationTestHttp.ClientOver(new StubHttpHandler(_ => NotificationTestHttp.Response(status, "nope")));

        var failure = await Assert.ThrowsAsync<NotificationDeliveryException>(() => http.SendAsync(Request(), "target", [], CancellationToken.None));

        Assert.Equal(transient, failure.IsTransient);
        Assert.Contains("nope", failure.Message);
        Assert.Contains(((int)status).ToString(), failure.Message);
    }

    [Fact]
    public async Task Http_ReadsOnlyThePermittedPartOfAResponse()
    {
        var large = new string('a', NotificationHttpClient.MaxResponseBytes + 4096);
        var http = NotificationTestHttp.ClientOver(new StubHttpHandler(_ => NotificationTestHttp.Response(HttpStatusCode.OK, large)));

        var body = await http.SendAsync(Request(), "target", [], CancellationToken.None);

        Assert.Equal(NotificationHttpClient.MaxResponseBytes, body.Length);
    }

    [Fact]
    public async Task Http_ErrorText_IsShortenedOntoOneLine_AndSecretsAreMasked()
    {
        var text = "line one secret-value\nline two " + new string('x', 2000);
        var http = NotificationTestHttp.ClientOver(new StubHttpHandler(_ => NotificationTestHttp.Response(HttpStatusCode.BadRequest, text)));

        var failure = await Assert.ThrowsAsync<NotificationDeliveryException>(
            () => http.SendAsync(Request(), "target", ["secret-value", null, ""], CancellationToken.None));

        Assert.DoesNotContain("secret-value", failure.Message);
        Assert.DoesNotContain('\n', failure.Message);
        Assert.True(failure.Message.Length < 500);
    }

    [Fact]
    public async Task Http_AnEmptyErrorBody_StillNamesTheStatus()
    {
        var http = NotificationTestHttp.ClientOver(new StubHttpHandler(_ => NotificationTestHttp.Response(HttpStatusCode.NotFound)));

        var failure = await Assert.ThrowsAsync<NotificationDeliveryException>(() => http.SendAsync(Request(), "target", [], CancellationToken.None));

        Assert.Equal("target answered with HTTP 404.", failure.Message);
    }

    [Fact]
    public async Task Http_HonoursRetryAfterAsDeltaOrDate_ButNotLongerThanAnHour()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);

        async Task<TimeSpan?> RetryAfterOf(Action<HttpResponseMessage> configure)
        {
            var handler = new StubHttpHandler(_ =>
            {
                var response = NotificationTestHttp.Response(HttpStatusCode.TooManyRequests);
                configure(response);
                return response;
            });
            var http = new NotificationHttpClient(new StubHttpClientFactory(handler), time);
            var failure = await Assert.ThrowsAsync<NotificationDeliveryException>(() => http.SendAsync(Request(), "target", [], CancellationToken.None));
            return failure.RetryAfter;
        }

        Assert.Equal(TimeSpan.FromSeconds(65), await RetryAfterOf(r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(65))));
        Assert.Equal(TimeSpan.FromMinutes(5), await RetryAfterOf(r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(now.AddMinutes(5))));
        Assert.Equal(TimeSpan.FromHours(1), await RetryAfterOf(r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(4))));
        Assert.Null(await RetryAfterOf(r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(now.AddMinutes(-5))));
        Assert.Null(await RetryAfterOf(_ => { }));
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "resolved")]
    [InlineData(HttpRequestError.ConnectionError, "connection failed")]
    [InlineData(HttpRequestError.SecureConnectionError, "TLS")]
    [InlineData(HttpRequestError.ProxyTunnelError, "proxy")]
    [InlineData(HttpRequestError.Unknown, "request failed")]
    public async Task Http_DescribesNetworkFailuresWithoutTheExceptionText(HttpRequestError error, string words)
    {
        var handler = new StubHttpHandler((Func<HttpRequestMessage, HttpResponseMessage>)(_ =>
            throw new HttpRequestException(error, "contains https://discord.com/api/webhooks/1/secret")));
        var http = NotificationTestHttp.ClientOver(handler);

        var failure = await Assert.ThrowsAsync<NotificationDeliveryException>(() => http.SendAsync(Request(), "target", [], CancellationToken.None));

        Assert.True(failure.IsTransient);
        Assert.Contains(words, failure.Message);
        Assert.DoesNotContain("secret", failure.Message);
    }

    [Fact]
    public async Task Http_ALostConnectionWhileReadingTheResponse_IsTemporary()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ThrowingContent() });
        var http = NotificationTestHttp.ClientOver(handler);

        var failure = await Assert.ThrowsAsync<NotificationDeliveryException>(() => http.SendAsync(Request(), "target", [], CancellationToken.None));

        Assert.True(failure.IsTransient);
        Assert.Contains("lost", failure.Message);
    }

    [Theory]
    [InlineData("with\nnewline")]
    [InlineData("with\rcarriage")]
    [InlineData("tökén")]
    public void Http_RefusesATokenThatCannotBeSentInAHeader(string token)
    {
        using var request = Request();

        Assert.Throws<NotificationNotConfiguredException>(() => NotificationHttpClient.AddSecretHeader(request, "X-Api-Key", token));
    }

    private static NotificationTester Tester(ILibraryActivityMonitor? activity, params RecordingChannel[] channels) =>
        new(channels, activity ?? new Mock<ILibraryActivityMonitor>().Object, Mock.Of<INotificationPublisher>());

    [Fact]
    public async Task Tester_SendsARealMessageEvenWhenTheTargetIsDisabled()
    {
        var channel = new RecordingChannel(NotificationTarget.Ntfy);
        var tester = Tester(null, channel);

        Assert.True(await tester.SendTestAsync(NotificationTarget.Ntfy, CancellationToken.None));

        var message = Assert.Single(channel.Sent);
        Assert.Equal(NotificationMessage.Test, message.EventType);
        Assert.Contains("test", message.Title);
    }

    [Fact]
    public async Task Tester_ATestOfARescanTarget_DescribesTheScan()
    {
        var channel = new RecordingChannel(NotificationTarget.Komga);

        await Tester(null, channel).SendTestAsync(NotificationTarget.Komga, CancellationToken.None);

        Assert.Contains("scan", Assert.Single(channel.Sent).Body);
    }

    [Fact]
    public async Task Tester_RefusesARescanWhileTheLibraryIsBusy_ButNotAMessage()
    {
        var activity = new Mock<ILibraryActivityMonitor>();
        activity.Setup(a => a.IsBusyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var komga = new RecordingChannel(NotificationTarget.Komga);
        var ntfy = new RecordingChannel(NotificationTarget.Ntfy);
        var tester = Tester(activity.Object, komga, ntfy);

        await Assert.ThrowsAsync<NotificationLibraryBusyException>(() => tester.SendTestAsync(NotificationTarget.Komga, CancellationToken.None));
        Assert.True(await tester.SendTestAsync(NotificationTarget.Ntfy, CancellationToken.None));

        Assert.Empty(komga.Sent);
        Assert.Single(ntfy.Sent);
    }

    [Fact]
    public async Task Tester_TellsTheQueueOnlyWhenTheTargetAcceptedTheTest()
    {
        var publisher = new Mock<INotificationPublisher>();
        var channel = new RecordingChannel(NotificationTarget.Ntfy);
        var tester = new NotificationTester(new[] { channel }, new Mock<ILibraryActivityMonitor>().Object, publisher.Object);

        channel.OnSend = (_, _) => throw new NotificationDeliveryException("no", isTransient: false);
        await Assert.ThrowsAsync<NotificationDeliveryException>(() => tester.SendTestAsync(NotificationTarget.Ntfy, CancellationToken.None));
        publisher.Verify(p => p.TargetVerified(It.IsAny<string>()), Times.Never);

        channel.OnSend = null;
        await tester.SendTestAsync(NotificationTarget.Ntfy, CancellationToken.None);
        publisher.Verify(p => p.TargetVerified(NotificationTarget.Ntfy), Times.Once);
    }

    [Fact]
    public async Task Tester_AnUnknownTargetIsReported() =>
        Assert.False(await Tester(null).SendTestAsync("email", CancellationToken.None));

    private sealed class ThrowingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            throw new IOException("connection reset");

        protected override Task<Stream> CreateContentReadStreamAsync() => throw new IOException("connection reset");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
