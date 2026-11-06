using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.WebApi.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Tests for <see cref="AutheliaHealthMonitor"/>. While Authelia is down the reverse proxy rejects every request
/// before it reaches ComicMaintainer, so these checks are the only record of the outage in the ComicMaintainer logs.
/// Each change is logged once (an ongoing outage again every five minutes), and never as an error, which the
/// error-reporting sink would file as a GitHub issue.
/// </summary>
public sealed class AutheliaHealthMonitorTests
{
    private const string AutheliaUrl = "http://authelia:9091";
    private const string HealthCheckUrl = "http://authelia:9091/api/health";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    // The startup extension classes are internal to the WebApi assembly (no InternalsVisibleTo).
    private static readonly Func<IServiceCollection, IServiceCollection> AddComicMaintainerHostedServices = typeof(Program).Assembly
        .GetType("ComicMaintainer.WebApi.Startup.CoreServiceCollectionExtensions", throwOnError: true)!
        .GetMethod("AddComicMaintainerHostedServices", BindingFlags.Static | BindingFlags.Public)!
        .CreateDelegate<Func<IServiceCollection, IServiceCollection>>();

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
    private readonly ListLogger _logger = new();
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly SemaphoreSlim _requestReceived = new(0);
    private Func<HttpRequestMessage, CancellationToken, Task<HttpStatusCode>> _respond = (_, _) => Respond(HttpStatusCode.OK);

    [Theory]
    [InlineData("http://authelia:9091", HealthCheckUrl)]
    [InlineData("http://authelia:9091/", HealthCheckUrl)]
    [InlineData("  http://authelia:9091  ", HealthCheckUrl)]
    [InlineData("https://auth.example.com", "https://auth.example.com/api/health")]
    [InlineData("https://example.com/authelia/", "https://example.com/authelia/api/health")]
    [InlineData("http://monitor@authelia:9091/authelia?redirect=1#top", "http://authelia:9091/authelia/api/health")]
    [InlineData("http://[fd00::5]:9091", "http://[fd00::5]:9091/api/health")]
    public void GetHealthCheckUri_AddsTheHealthEndpointToTheAddress(string url, string expected)
    {
        Assert.Equal(expected, AutheliaHealthMonitor.GetHealthCheckUri(url)?.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("authelia:9091")]
    [InlineData("authelia")]
    [InlineData("/authelia")]
    [InlineData("ftp://authelia:9091")]
    [InlineData("file:///authelia")]
    public void GetHealthCheckUri_AcceptsOnlyAnHttpOrHttpsUrl(string? url)
    {
        Assert.Null(AutheliaHealthMonitor.GetHealthCheckUri(url));
    }

    [Fact]
    public async Task ReachableAuthelia_IsLoggedOnce()
    {
        var monitor = CreateMonitor();

        await monitor.CheckAsync(CancellationToken.None);
        await CheckAgainAsync(monitor);

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal($"Authelia is reachable at {HealthCheckUrl}", entry.Message);
        Assert.Equal(2, _requests.Count);
        Assert.All(_requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(HealthCheckUrl, request.Uri);
            Assert.True(request.IsHealthCheck);
        });
    }

    [Fact]
    public async Task Outage_IsWarnedAboutAtTheSecondFailedCheck_ThenEveryFiveMinutes_AndItsLengthIsLoggedOnRecovery()
    {
        var monitor = CreateMonitor();
        await monitor.CheckAsync(CancellationToken.None);
        _respond = (_, _) => Respond(HttpStatusCode.ServiceUnavailable);

        await CheckAgainAsync(monitor);
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Warning);

        await CheckAgainAsync(monitor);
        var outage = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(
            $"Authelia stopped responding at {HealthCheckUrl}: HTTP 503 Service Unavailable. Until it recovers, the reverse proxy rejects requests to ComicMaintainer, usually with HTTP 500 or 502; ComicMaintainer itself is still running",
            outage.Message);

        // The next nine checks fall within five minutes of the warning.
        await CheckAgainAsync(monitor, times: 9);
        Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);

        // The downtime counts from the last successful check, six minutes before the reminder.
        await CheckAgainAsync(monitor);
        Assert.Equal(
            (LogLevel.Warning, $"Authelia has not been reachable at {HealthCheckUrl} for 6 minutes: HTTP 503 Service Unavailable"),
            LastEntry());

        await CheckAgainAsync(monitor, times: 10);
        Assert.Equal(
            (LogLevel.Warning, $"Authelia has not been reachable at {HealthCheckUrl} for 11 minutes: HTTP 503 Service Unavailable"),
            LastEntry());

        _respond = (_, _) => Respond(HttpStatusCode.OK);
        await CheckAgainAsync(monitor, times: 2);

        Assert.Equal(
            new (LogLevel, string)[]
            {
                (LogLevel.Information, $"Authelia is reachable at {HealthCheckUrl}"),
                (LogLevel.Warning, outage.Message),
                (LogLevel.Warning, $"Authelia has not been reachable at {HealthCheckUrl} for 6 minutes: HTTP 503 Service Unavailable"),
                (LogLevel.Warning, $"Authelia has not been reachable at {HealthCheckUrl} for 11 minutes: HTTP 503 Service Unavailable"),
                (LogLevel.Information, $"Authelia is reachable again at {HealthCheckUrl}; it was unreachable for 11 minutes"),
            },
            _logger.Entries.Where(e => e.Level >= LogLevel.Information).Select(e => (e.Level, e.Message)));
        AssertNoErrorsOrStackTraces();
    }

    [Fact]
    public async Task SingleFailedCheck_IsNotWarnedAbout()
    {
        // Authelia restarting, or a dropped packet, fails one check.
        var monitor = CreateMonitor();
        await monitor.CheckAsync(CancellationToken.None);
        _respond = (_, _) => Task.FromException<HttpStatusCode>(new HttpRequestException("Connection refused (authelia:9091)"));
        await CheckAgainAsync(monitor);
        _respond = (_, _) => Respond(HttpStatusCode.OK);
        await CheckAgainAsync(monitor);

        // Every other check failing is not warned about either.
        for (var i = 0; i < 5; i++)
        {
            _respond = (_, _) => Respond(HttpStatusCode.BadGateway);
            await CheckAgainAsync(monitor);
            _respond = (_, _) => Respond(HttpStatusCode.OK);
            await CheckAgainAsync(monitor);
        }

        Assert.Equal(
            new[] { (LogLevel.Information, $"Authelia is reachable at {HealthCheckUrl}") },
            _logger.Entries.Where(e => e.Level >= LogLevel.Information).Select(e => (e.Level, e.Message)));
        Assert.Equal(
            $"Authelia did not answer at {HealthCheckUrl}: Connection refused (authelia:9091). Checking again in 30 seconds",
            _logger.Entries[1].Message);
        Assert.Equal(LogLevel.Debug, _logger.Entries[1].Level);
    }

    [Fact]
    public async Task ShortestOutage_IsLoggedAsAMinute_WhenTheTimerTicksEarly()
    {
        var monitor = CreateMonitor();
        _respond = (_, _) => Respond(HttpStatusCode.BadGateway);
        await monitor.CheckAsync(CancellationToken.None);
        await CheckAgainAsync(monitor);
        _respond = (_, _) => Respond(HttpStatusCode.OK);

        // The timer ticks a few milliseconds early or late: 59.995 seconds is reported as the minute it stands for.
        _clock.Advance(AutheliaHealthMonitor.CheckInterval - TimeSpan.FromMilliseconds(5));
        await monitor.CheckAsync(CancellationToken.None);

        Assert.Equal(
            (LogLevel.Information, $"Authelia is reachable again at {HealthCheckUrl}; it was unreachable for 1 minute"),
            LastEntry());
    }

    [Fact]
    public async Task Reminder_IsNotPutOff_WhenTheCheckThatWarnedStartedLate()
    {
        var monitor = CreateMonitor();
        await monitor.CheckAsync(CancellationToken.None);
        _respond = (_, _) => Respond(HttpStatusCode.ServiceUnavailable);
        await CheckAgainAsync(monitor);

        // The check that warns starts 20 ms after its tick, the tenth one after it on time: 4:59.98 later.
        _clock.Advance(AutheliaHealthMonitor.CheckInterval + TimeSpan.FromMilliseconds(20));
        await monitor.CheckAsync(CancellationToken.None);
        _clock.Advance(AutheliaHealthMonitor.CheckInterval - TimeSpan.FromMilliseconds(20));
        await monitor.CheckAsync(CancellationToken.None);
        await CheckAgainAsync(monitor, times: 9);

        Assert.Equal(2, _logger.Entries.Count(e => e.Level == LogLevel.Warning));
        Assert.StartsWith($"Authelia has not been reachable at {HealthCheckUrl} for 6 minutes", LastEntry().Message);
    }

    [Fact]
    public async Task FailedFirstCheck_IsNotWarnedAbout_WhenAutheliaAnswersTheNextOne()
    {
        // Authelia may be starting at the same time as ComicMaintainer, after a reboot for example.
        var monitor = CreateMonitor();
        _respond = (_, _) => Task.FromException<HttpStatusCode>(new HttpRequestException("Connection refused (authelia:9091)"));

        await monitor.CheckAsync(CancellationToken.None);

        var notYet = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Debug, notYet.Level);

        _respond = (_, _) => Respond(HttpStatusCode.OK);
        await CheckAgainAsync(monitor);

        Assert.Equal((LogLevel.Information, $"Authelia is reachable at {HealthCheckUrl}"), LastEntry());
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task AutheliaUnreachableFromTheStart_IsWarnedAboutOnTheSecondCheck()
    {
        var monitor = CreateMonitor();
        _respond = (_, _) => Task.FromException<HttpStatusCode>(new HttpRequestException("Connection refused (authelia:9091)"));

        await monitor.CheckAsync(CancellationToken.None);
        await CheckAgainAsync(monitor);

        var warning = Assert.Single(_logger.Entries, e => e.Level >= LogLevel.Information);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(
            $"Authelia is not reachable at {HealthCheckUrl}: Connection refused (authelia:9091). While Authelia is down, the reverse proxy rejects requests to ComicMaintainer, usually with HTTP 500 or 502. If Authelia is running, check that AUTHELIA_URL is an address this container can reach",
            warning.Message);

        // The reminder comes five minutes after the warning and counts the downtime from the first check.
        await CheckAgainAsync(monitor, times: 10);

        Assert.Equal(
            (LogLevel.Warning, $"Authelia has not been reachable at {HealthCheckUrl} for 5 minutes: Connection refused (authelia:9091)"),
            LastEntry());
        AssertNoErrorsOrStackTraces();
    }

    [Theory]
    [InlineData("not-found", "HTTP 404 Not Found")]
    [InlineData("refused", "Connection refused (authelia:9091)")]
    [InlineData("tls", "The SSL connection could not be established, see inner exception. The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot")]
    [InlineData("multi-line", "first line\\nsecond line")]
    public async Task Outage_IsLoggedWithWhatWentWrong(string failure, string reason)
    {
        var monitor = CreateMonitor();
        await monitor.CheckAsync(CancellationToken.None);
        _respond = (_, _) => failure switch
        {
            "not-found" => Respond(HttpStatusCode.NotFound),
            // The inner exception repeats the message, so it is left out.
            "refused" => Task.FromException<HttpStatusCode>(new HttpRequestException(
                "Connection refused (authelia:9091)", new IOException("Connection refused"))),
            "tls" => Task.FromException<HttpStatusCode>(new HttpRequestException(
                "The SSL connection could not be established, see inner exception.",
                new AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot"))),
            "multi-line" => Task.FromException<HttpStatusCode>(new InvalidOperationException("first line\nsecond line")),
            _ => throw new UnreachableException(),
        };

        await CheckAgainAsync(monitor, times: AutheliaHealthMonitor.FailedChecksBeforeWarning);

        var warning = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.StartsWith($"Authelia stopped responding at {HealthCheckUrl}: {reason}. Until it recovers", warning.Message);
        AssertNoErrorsOrStackTraces();
    }

    [Fact]
    public async Task UnansweredCheck_TimesOutAndCountsAsAnOutage()
    {
        var monitor = CreateMonitor();
        await monitor.CheckAsync(CancellationToken.None);

        for (var i = 0; i < AutheliaHealthMonitor.FailedChecksBeforeWarning; i++)
        {
            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _respond = async (_, cancellationToken) =>
            {
                sent.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new UnreachableException();
            };
            _clock.Advance(AutheliaHealthMonitor.CheckInterval - AutheliaHealthMonitor.RequestTimeout);

            var check = monitor.CheckAsync(CancellationToken.None);
            await sent.Task.WaitAsync(WaitLimit);
            _clock.Advance(AutheliaHealthMonitor.RequestTimeout);
            await check.WaitAsync(WaitLimit);
        }

        var warning = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.StartsWith($"Authelia stopped responding at {HealthCheckUrl}: no response within 10 seconds. Until it recovers", warning.Message);
        AssertNoErrorsOrStackTraces();
    }

    [Fact]
    public async Task CheckCancelledByShutdown_IsNotLoggedAsAnOutage()
    {
        var monitor = CreateMonitor();
        await monitor.CheckAsync(CancellationToken.None);
        using var shutdown = new CancellationTokenSource();
        _respond = async (_, cancellationToken) =>
        {
            await shutdown.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            throw new UnreachableException();
        };
        _clock.Advance(AutheliaHealthMonitor.CheckInterval);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.CheckAsync(shutdown.Token));

        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task WithoutAnUrl_NothingIsChecked_AndEnabledAutheliaGetsADebugHint(string? url)
    {
        using var monitor = CreateMonitor(url, autheliaEnabled: true);

        await RunUntilFinishedAsync(monitor);

        // Debug, so it stays out of the container log and app.log of every deployment that does not set AUTHELIA_URL.
        var hint = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Debug, hint.Level);
        Assert.StartsWith("Set AUTHELIA_URL to Authelia's address, such as http://authelia:9091, to have Authelia outages logged", hint.Message);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task WithoutAnUrl_AndAutheliaDisabled_NothingIsCheckedOrLogged()
    {
        using var monitor = CreateMonitor(url: null, autheliaEnabled: false);

        await RunUntilFinishedAsync(monitor);

        Assert.Empty(_logger.Entries);
        Assert.Empty(_requests);
    }

    [Theory]
    [InlineData("authelia:9091")]
    [InlineData("ftp://authelia")]
    // The value is not logged, as it may hold credentials or a token.
    [InlineData("monitor@authelia:9091/?token=do-not-log")]
    [InlineData("ftp://monitor@authelia:9091/?token=do-not-log")]
    public async Task WithAnInvalidUrl_NothingIsChecked_AndAWarningLeavesTheUrlOut(string url)
    {
        using var monitor = CreateMonitor(url, autheliaEnabled: false);

        await RunUntilFinishedAsync(monitor);

        Assert.Null(monitor.HealthCheckUri);
        var warning = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(
            "AUTHELIA_URL is not an http:// or https:// URL, such as http://authelia:9091, so Authelia outages will not be logged",
            warning.Message);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task CredentialsInTheUrl_AreNeitherSentNorLogged()
    {
        var monitor = CreateMonitor("http://monitor@authelia:9091/?token=do-not-log");
        await monitor.CheckAsync(CancellationToken.None);
        _respond = (_, _) => Task.FromException<HttpStatusCode>(new HttpRequestException("Connection refused (authelia:9091)"));
        await CheckAgainAsync(monitor, times: 2);
        _respond = (_, _) => Respond(HttpStatusCode.OK);
        await CheckAgainAsync(monitor);

        Assert.Equal(
            new[] { LogLevel.Information, LogLevel.Debug, LogLevel.Warning, LogLevel.Information },
            _logger.Entries.Select(e => e.Level));
        Assert.All(_logger.Entries, e =>
        {
            Assert.Contains(HealthCheckUrl, e.Message);
            Assert.DoesNotContain("monitor@", e.Message);
            Assert.DoesNotContain("do-not-log", e.Message);
        });
        Assert.All(_requests, request => Assert.Equal(HealthCheckUrl, request.Uri));
    }

    [Fact]
    public async Task Started_ChecksAtOnceAndThenEveryInterval_WhetherOrNotAutheliaAuthenticationIsEnabled()
    {
        // Authelia can protect the app at the proxy while the app itself uses its own sign-in.
        using var monitor = CreateMonitor(autheliaEnabled: false);

        await monitor.StartAsync(CancellationToken.None);
        await WaitForRequestAsync();
        _clock.Advance(AutheliaHealthMonitor.CheckInterval);
        await WaitForRequestAsync();
        await monitor.StopAsync(CancellationToken.None);
        await monitor.ExecuteTask!.WaitAsync(WaitLimit);

        Assert.True(monitor.ExecuteTask.IsCompletedSuccessfully);
        Assert.Equal(2, _requests.Count);
        Assert.Equal(
            new[] { $"Checking Authelia at {HealthCheckUrl} every 30 seconds", $"Authelia is reachable at {HealthCheckUrl}" },
            _logger.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Registration_AddsTheMonitor_WithAClientThatDoesNotLogEachCheck()
    {
        var logs = new CategoryRecordingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));

        AddComicMaintainerHostedServices(services);

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AutheliaHealthMonitor));
        services.AddHttpClient(AutheliaHealthMonitor.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new StubHandler(this));
        // A client registered the usual way, to show that the request logging would otherwise be recorded.
        services.AddHttpClient("Control").ConfigurePrimaryHttpMessageHandler(() => new StubHandler(this));
        await using var provider = services.BuildServiceProvider();
        var clients = provider.GetRequiredService<IHttpClientFactory>();

        using var check = await clients.CreateClient(AutheliaHealthMonitor.HttpClientName).GetAsync(HealthCheckUrl);
        using var control = await clients.CreateClient("Control").GetAsync(HealthCheckUrl);

        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        Assert.Equal("ComicMaintainer/1.0 (+https://github.com/mleenorris/ComicMaintainer)", _requests.First().UserAgent);
        Assert.Contains(logs.Categories, c => c.StartsWith("System.Net.Http.HttpClient.Control.", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Categories, c => c.StartsWith($"System.Net.Http.HttpClient.{AutheliaHealthMonitor.HttpClientName}.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Registration_KeepsTheChecksOutOfTheHttpClientMetrics()
    {
        // Real handlers, as the metrics are recorded inside SocketsHttpHandler; each server answers one request.
        using var checkServer = StartServer(out var checkPort, out var checkServed);
        using var controlServer = StartServer(out var controlPort, out var controlServed);
        var measurements = new ConcurrentQueue<(string Meter, int? Port)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name is "System.Net.Http" or AutheliaHealthMonitor.UnobservedMeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => measurements.Enqueue((instrument.Meter.Name, PortOf(tags))));
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => measurements.Enqueue((instrument.Meter.Name, PortOf(tags))));
        listener.Start();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        AddComicMaintainerHostedServices(services);
        await using var provider = services.BuildServiceProvider();
        var clients = provider.GetRequiredService<IHttpClientFactory>();

        using (var check = await clients.CreateClient(AutheliaHealthMonitor.HttpClientName).GetAsync($"http://127.0.0.1:{checkPort}/api/health"))
        using (var control = await clients.CreateClient().GetAsync($"http://127.0.0.1:{controlPort}/api/health"))
        {
            Assert.Equal(HttpStatusCode.OK, check.StatusCode);
            Assert.Equal(HttpStatusCode.OK, control.StatusCode);
        }

        await Task.WhenAll(checkServed, controlServed).WaitAsync(WaitLimit);
        Assert.Contains(measurements, m => m == ("System.Net.Http", controlPort));
        Assert.Contains(measurements, m => m == (AutheliaHealthMonitor.UnobservedMeterName, checkPort));
        Assert.DoesNotContain(measurements, m => m == ("System.Net.Http", checkPort));
    }

    private static int? PortOf(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == "server.port" && tag.Value is int port)
            {
                return port;
            }
        }

        return null;
    }

    /// <summary>A loopback HTTP server that answers one request with an empty 200.</summary>
    private static TcpListener StartServer(out int port, out Task served)
    {
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        port = ((IPEndPoint)server.LocalEndpoint).Port;
        served = ServeOneAsync(server);
        return server;

        static async Task ServeOneAsync(TcpListener server)
        {
            using var client = await server.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var received = new StringBuilder();
            var buffer = new byte[4096];
            while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    return;
                }

                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
        }
    }

    private AutheliaHealthMonitor CreateMonitor(string? url = AutheliaUrl, bool autheliaEnabled = true)
    {
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        clients.Setup(f => f.CreateClient(AutheliaHealthMonitor.HttpClientName)).Returns(() => new HttpClient(new StubHandler(this)));
        return new AutheliaHealthMonitor(
            Options.Create(new AutheliaSettings { Url = url, Enabled = autheliaEnabled }),
            clients.Object,
            _logger,
            _clock);
    }

    private async Task CheckAgainAsync(AutheliaHealthMonitor monitor, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            _clock.Advance(AutheliaHealthMonitor.CheckInterval);
            await monitor.CheckAsync(CancellationToken.None);
        }
    }

    private static async Task RunUntilFinishedAsync(AutheliaHealthMonitor monitor)
    {
        await monitor.StartAsync(CancellationToken.None);
        await monitor.ExecuteTask!.WaitAsync(WaitLimit);
        Assert.True(monitor.ExecuteTask.IsCompletedSuccessfully);
    }

    private async Task WaitForRequestAsync() =>
        Assert.True(await _requestReceived.WaitAsync(WaitLimit), "Authelia was not checked");

    private (LogLevel Level, string Message) LastEntry()
    {
        var entry = _logger.Entries.Last();
        return (entry.Level, entry.Message);
    }

    /// <summary>Errors are filed as GitHub issues, and an outage is not a defect; stack traces would only add noise.</summary>
    private void AssertNoErrorsOrStackTraces()
    {
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Error);
        Assert.All(_logger.Entries, e => Assert.Null(e.Exception));
    }

    private static Task<HttpStatusCode> Respond(HttpStatusCode status) => Task.FromResult(status);

    private sealed record RecordedRequest(HttpMethod Method, string? Uri, bool IsHealthCheck, string UserAgent);

    private sealed class StubHandler(AutheliaHealthMonitorTests test) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            test._requests.Enqueue(new RecordedRequest(
                request.Method,
                request.RequestUri?.AbsoluteUri,
                AutheliaHealthMonitor.IsHealthCheck(request),
                request.Headers.UserAgent.ToString()));
            test._requestReceived.Release();
            return new HttpResponseMessage(await test._respond(request, cancellationToken));
        }
    }

    private sealed class ListLogger : ILogger<AutheliaHealthMonitor>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception), exception));
    }

    /// <summary>Records the categories that log anything.</summary>
    private sealed class CategoryRecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentDictionary<string, bool> _categories = new();

        public IReadOnlyCollection<string> Categories => _categories.Keys.ToArray();

        public ILogger CreateLogger(string categoryName) => new CategoryLogger(categoryName, _categories);

        public void Dispose()
        {
        }

        private sealed class CategoryLogger(string category, ConcurrentDictionary<string, bool> categories) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                categories.TryAdd(category, true);
        }
    }
}
