using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.ErrorReporting.Interfaces;
using ComicMaintainer.Core.ErrorReporting.Models;
using ComicMaintainer.Core.ErrorReporting.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace ComicMaintainer.Tests.ErrorReporting;

/// <summary>
/// Covers the pipeline as a whole: the opt-out default, deduplication,
/// redaction being applied before anything is stored, and the throttles that
/// stop a failing instance from flooding the issue tracker.
/// </summary>
public class ErrorReportServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly AppSettings _settings = new()
    {
        EnableErrorReporting = true,
        ErrorReportingMode = "manual",
        GitHubRepository = "mleenorris/ComicMaintainer",
        ErrorReportMaxPerDay = 5,
        ErrorReportCooldownHours = 24,
        ErrorReportLogContextLines = 40,
    };

    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _factory = null!;
    private RecordingTransport _transport = null!;
    private RecordingTransport _consentTransport = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        _factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private ErrorReportService CreateService(
        bool automatic = false,
        bool transportFails = false,
        string appVersion = "2.0.310")
    {
        // Both transports are registered, as they are in Program.cs, so that
        // mode selection and the non-transmitting fallback are exercised the
        // way they behave in production.
        _transport = new RecordingTransport(automatic: true, succeeds: !transportFails);
        _consentTransport = new RecordingTransport(automatic: false);
        _settings.ErrorReportingMode = automatic ? "automatic" : "manual";

        var monitor = new Mock<IOptionsMonitor<AppSettings>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(_settings);

        return new ErrorReportService(
            _factory,
            monitor.Object,
            new ErrorReportRedactor(),
            [_consentTransport, _transport],
            new ErrorReportLogBuffer(),
            new Mock<ILogger<ErrorReportService>>().Object,
            _time,
            appVersion);
    }

    [Fact]
    public async Task CaptureAsync_DoesNothingWhenReportingIsDisabled()
    {
        // Off by default is the whole privacy posture of this feature: an
        // instance must not accumulate diagnostic data unless asked to.
        _settings.EnableErrorReporting = false;
        var service = CreateService();

        var report = await service.CaptureAsync(new NullReferenceException("boom"), ErrorReportSource.Api);

        Assert.Null(report);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(db.ErrorReports);
    }

    [Fact]
    public async Task CaptureAsync_StoresRedactedReport()
    {
        var service = CreateService();

        var report = await service.CaptureAsync(
            new InvalidOperationException("Cannot read /home/alice/Comics/Batman 001.cbz"),
            ErrorReportSource.Api,
            "GET /api/files");

        Assert.NotNull(report);
        Assert.DoesNotContain("alice", report!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Batman", report.Message, StringComparison.OrdinalIgnoreCase);

        // And the same must be true of what was written to disk, not just of
        // the object returned to the caller.
        await using var db = await _factory.CreateDbContextAsync();
        var stored = await db.ErrorReports.SingleAsync();
        Assert.DoesNotContain("alice", stored.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CaptureAsync_SuppressesEnvironmentalFailures()
    {
        var service = CreateService();

        Assert.Null(await service.CaptureAsync(new TaskCanceledException(), ErrorReportSource.Background));
        Assert.Null(await service.CaptureAsync(
            new InvalidOperationException("database is locked"), ErrorReportSource.Background));

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(db.ErrorReports);
    }

    [Fact]
    public async Task CaptureAsync_DeduplicatesRecurrencesIntoOneRecord()
    {
        var service = CreateService();

        for (var i = 0; i < 4; i++)
        {
            await service.CaptureAsync("System.NullReferenceException", "boom", Trace, ErrorReportSource.Api);
            _time.Advance(TimeSpan.FromMinutes(5));
        }

        await using var db = await _factory.CreateDbContextAsync();
        var stored = await db.ErrorReports.SingleAsync();

        Assert.Equal(4, stored.OccurrenceCount);
        Assert.Equal(new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc), stored.FirstSeenAt);
        Assert.Equal(new DateTime(2026, 9, 29, 12, 15, 0, DateTimeKind.Utc), stored.LastSeenAt);
    }

    [Fact]
    public async Task CaptureAsync_KeepsDistinctDefectsApart()
    {
        var service = CreateService();

        await service.CaptureAsync("System.NullReferenceException", "boom", Trace, ErrorReportSource.Api);
        await service.CaptureAsync("System.InvalidOperationException", "boom", Trace, ErrorReportSource.Api);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await db.ErrorReports.CountAsync());
    }

    [Fact]
    public async Task SubmitAsync_ConsentTransportDoesNotTransmit()
    {
        var service = CreateService(automatic: false);
        var report = await service.CaptureAsync("System.Exception", "boom", Trace, ErrorReportSource.Api);

        var result = await service.SubmitAsync(report!.Fingerprint);

        Assert.False(result.Delivered);
        Assert.Equal(1, _consentTransport.SendCount);
        Assert.Equal(0, _transport.SendCount);
    }

    [Fact]
    public async Task CaptureAsync_TransmitsImmediatelyInAutomaticMode()
    {
        var service = CreateService(automatic: true);

        var report = await service.CaptureAsync("System.Exception", "boom", Trace, ErrorReportSource.Api);

        Assert.NotNull(report);
        Assert.Equal(1, _transport.SendCount);
    }

    [Fact]
    public async Task SubmitAsync_HonoursCooldownForAutomaticTransport()
    {
        // Capture delivers the first report, which starts the cooldown. A
        // second delivery inside the window would turn a recurring defect into
        // a stream of duplicate comments on the same issue.
        var service = CreateService(automatic: true);
        var report = await service.CaptureAsync("System.Exception", "boom", Trace, ErrorReportSource.Api);

        Assert.Equal(1, _transport.SendCount);

        _time.Advance(TimeSpan.FromHours(1));
        var second = await service.SubmitAsync(report!.Fingerprint);

        Assert.False(second.Delivered);
        Assert.Equal(1, _transport.SendCount);
        Assert.Contains("cooling down", second.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitAsync_ReportsAgainAfterCooldownExpires()
    {
        var service = CreateService(automatic: true);
        var report = await service.CaptureAsync("System.Exception", "boom", Trace, ErrorReportSource.Api);

        _time.Advance(TimeSpan.FromHours(25));

        Assert.True((await service.SubmitAsync(report!.Fingerprint)).Delivered);
        Assert.Equal(2, _transport.SendCount);
    }

    [Fact]
    public async Task SubmitAsync_EnforcesDailyCap()
    {
        _settings.ErrorReportMaxPerDay = 2;
        var service = CreateService(automatic: true);

        for (var i = 0; i < 4; i++)
        {
            await service.CaptureAsync(
                $"System.Exception{i}", "boom", $"   at ComicMaintainer.Core.S{i}.M()", ErrorReportSource.Api);
        }

        // Four distinct defects, each of which would file its own issue, but
        // the instance is only allowed two deliveries a day.
        Assert.Equal(2, _transport.SendCount);
    }

    [Fact]
    public async Task SubmitAsync_DoesNotTransmitWhenReportingIsDisabled()
    {
        // Turning reporting off must stop delivery of reports that were
        // already captured, not merely stop new captures.
        var service = CreateService(automatic: false);
        var report = await service.CaptureAsync("System.Exception", "boom", Trace, ErrorReportSource.Api);

        _settings.EnableErrorReporting = false;
        _settings.ErrorReportingMode = "automatic";

        var result = await service.SubmitAsync(report!.Fingerprint);

        Assert.False(result.Delivered);
        Assert.Equal(0, _transport.SendCount);
    }

    [Fact]
    public async Task SubmitAsync_FallsBackToNonTransmittingTransportForUnknownMode()
    {
        // An unrecognised mode must never be read as permission to send. The
        // mode is set *after* CreateService, which otherwise overwrites it with
        // "automatic" and leaves the fallback untested.
        var service = CreateService(automatic: true);
        _settings.ErrorReportingMode = "something-unexpected";

        var report = await service.CaptureAsync("System.Exception", "boom", Trace, ErrorReportSource.Api);

        var result = await service.SubmitAsync(report!.Fingerprint);

        Assert.False(result.Delivered);
        // The decisive assertion: the transmitting transport was never reached,
        // during capture or during the explicit submit.
        Assert.Equal(0, _transport.SendCount);
        Assert.Equal(1, _consentTransport.SendCount);
    }

    [Fact]
    public async Task CaptureAsync_DoesNotAutoTransmitFrontendReports()
    {
        // `POST /api/errorreports/client` takes arbitrary text from any
        // signed-in user, read-only ones included. Publishing that under the
        // administrator's token without review would hand attacker-authored
        // content straight to the public issue tracker and the agent workflow.
        var service = CreateService(automatic: true);

        await service.CaptureAsync(
            "TypeError", "browser boom", Trace, ErrorReportSource.Frontend);

        Assert.Equal(0, _transport.SendCount);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.ErrorReports.CountAsync());
        Assert.Empty(db.ErrorReportDeliveries);
    }

    [Fact]
    public async Task SubmitAsync_DeliversFrontendReportsWhenAnAdministratorAsks()
    {
        var service = CreateService(automatic: true);

        var report = await service.CaptureAsync(
            "TypeError", "browser boom", Trace, ErrorReportSource.Frontend);

        Assert.True((await service.SubmitAsync(report!.Fingerprint)).Delivered);
        Assert.Equal(1, _transport.SendCount);
    }

    [Fact]
    public async Task SubmitAsync_CountsEveryDeliveryAgainstTheDailyCap()
    {
        // The cap limits transmissions, not distinct defects. Counting rows by
        // "last reported" would let repeated deliveries of one fingerprint
        // pass as a single report once the cooldown is short.
        _settings.ErrorReportMaxPerDay = 2;
        _settings.ErrorReportCooldownHours = 1;

        var service = CreateService(automatic: true);
        var report = await service.CaptureAsync("System.Exception", "boom", Trace, ErrorReportSource.Api);

        _time.Advance(TimeSpan.FromHours(2));
        Assert.True((await service.SubmitAsync(report!.Fingerprint)).Delivered);

        _time.Advance(TimeSpan.FromHours(2));
        var third = await service.SubmitAsync(report.Fingerprint);

        Assert.False(third.Delivered);
        Assert.Contains("cap", third.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, _transport.SendCount);
    }

    [Fact]
    public async Task SubmitAsync_ReleasesTheReservationWhenDeliveryFails()
    {
        // A failed send must not cost the user their daily slot or start a
        // cooldown against an issue that was never filed.
        _settings.ErrorReportMaxPerDay = 1;
        var service = CreateService(automatic: true, transportFails: true);

        var report = await service.CaptureAsync("System.Exception", "boom", Trace, ErrorReportSource.Api);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Empty(db.ErrorReportDeliveries);
            Assert.Null((await db.ErrorReports.SingleAsync()).LastReportedAt);
        }

        // And the next attempt is still allowed through.
        Assert.Equal(1, _transport.SendCount);
        await service.SubmitAsync(report!.Fingerprint);
        Assert.Equal(2, _transport.SendCount);
    }

    [Fact]
    public async Task Fingerprint_ChangesWithTheRunningApplicationVersion()
    {
        // The release version lives in ComicMaintainer.WebApi.csproj, so the
        // fingerprint has to follow the running application rather than the
        // Core assembly: the same trace in a later release is a regression, not
        // a duplicate of a closed issue.
        var first = await CreateService(appVersion: "2.0.310")
            .CaptureAsync("System.Exception", "boom", Trace, ErrorReportSource.Api);
        var second = await CreateService(appVersion: "2.0.311")
            .CaptureAsync("System.Exception", "boom", Trace, ErrorReportSource.Api);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.Fingerprint, second!.Fingerprint);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await db.ErrorReports.CountAsync());
    }

    [Fact]
    public async Task CaptureAsync_CollapsesConcurrentOccurrencesOntoOneRow()
    {
        // Capture runs concurrently from request handlers, the browser endpoint
        // and the sink dispatcher. Two of them racing the unique index must
        // produce one row with both occurrences, not a lost write.
        var service = CreateService();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            service.CaptureAsync("System.Exception", "boom", Trace, ErrorReportSource.Api)));

        await using var db = await _factory.CreateDbContextAsync();
        var stored = await db.ErrorReports.SingleAsync();

        Assert.Equal(8, stored.OccurrenceCount);
    }

    [Fact]
    public async Task DismissAsync_RemovesSuppressionSoRegressionsReportAgain()
    {
        var service = CreateService();
        var report = await service.CaptureAsync("System.Exception", "boom", Trace, ErrorReportSource.Api);

        Assert.True(await service.DismissAsync(report!.Fingerprint));

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(db.ErrorReports);

        await service.CaptureAsync("System.Exception", "boom", Trace, ErrorReportSource.Api);
        await using var db2 = await _factory.CreateDbContextAsync();
        var refiled = await db2.ErrorReports.SingleAsync();
        Assert.Equal(1, refiled.OccurrenceCount);
    }

    [Fact]
    public async Task PreviewIssueBodyAsync_ReturnsExactlyWhatWouldBeSent()
    {
        var service = CreateService();
        var report = await service.CaptureAsync("System.Exception", "boom", Trace, ErrorReportSource.Api);

        var preview = await service.PreviewIssueBodyAsync(report!.Fingerprint);

        Assert.NotNull(preview);
        Assert.Contains(ErrorReportIssueBuilder.Marker(report.Fingerprint), preview!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewIssueBodyAsync_ReturnsNullForUnknownFingerprint()
    {
        var service = CreateService();
        Assert.Null(await service.PreviewIssueBodyAsync("0000000000000000"));
    }

    private const string Trace = "   at ComicMaintainer.Core.Services.SeriesLibraryService.Scan()";

    /// <summary>
    /// Stand-in transport that records calls instead of reaching the network.
    /// </summary>
    private sealed class RecordingTransport : IErrorReportTransport
    {
        private readonly bool _succeeds;

        public RecordingTransport(bool automatic, bool succeeds = true)
        {
            TransmitsAutomatically = automatic;
            _succeeds = succeeds;
        }

        public string Mode => TransmitsAutomatically ? "automatic" : "manual";

        public bool TransmitsAutomatically { get; }

        public int SendCount { get; private set; }

        public Task<ErrorReportTransportResult> SendAsync(
            ErrorReport report,
            CancellationToken cancellationToken = default)
        {
            SendCount++;

            var delivered = TransmitsAutomatically && _succeeds;

            return Task.FromResult(new ErrorReportTransportResult(
                delivered, delivered ? SendCount : null, "https://example/1", "ok"));
        }
    }
}
