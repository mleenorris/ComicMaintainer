using System.Text.Json;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// The operator view over the error-reporting ledger: listing and filtering
/// recorded failures, muting/unmuting fingerprints, and the test report that
/// checks the GitHub configuration end to end.
/// </summary>
public class ErrorReportsControllerTests
{
    private const string TestMessage = "ComicMaintainer error reporting test report requested by an administrator";
    private static readonly DateTime BaseTime = new(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly Mock<IErrorReportFactory> _reportFactory = new();
    private readonly Mock<IErrorReportingService> _reportingService = new();

    public ErrorReportsControllerTests()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseInMemoryDatabase($"error-reports-{Guid.NewGuid():N}"));
        _dbContextFactory = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
    }

    // ─── Listing ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetReports_ListsNewestFirstWithTheTotalBeforePaging()
    {
        await SeedAsync(
            Report("fp-old", lastSeenHoursAgo: 30),
            Report("fp-newest", lastSeenHoursAgo: 1, r =>
            {
                r.Level = "Fatal";
                r.ExceptionType = "System.IO.IOException";
                r.SourceContext = "ComicMaintainer.Core.Services.FileStoreService";
                r.RenderedMessage = "Disk full";
                r.OccurrenceCount = 7;
                r.State = ErrorReportState.Reported;
                r.GitHubIssueNumber = 321;
                r.GitHubIssueUrl = "https://github.test/issues/321";
                r.LastReportedAt = BaseTime.AddHours(-2);
                r.LastError = "previous attempt timed out";
            }),
            Report("fp-middle", lastSeenHoursAgo: 5));

        var json = OkJson(await CreateController().GetReports(limit: 2));

        Assert.Equal(3, json.GetProperty("total").GetInt32());
        var reports = json.GetProperty("reports").EnumerateArray().ToList();
        Assert.Equal(new[] { "fp-newest", "fp-middle" }, reports.Select(r => r.GetProperty("fingerprint").GetString()));

        var newest = reports[0];
        Assert.Equal("Fatal", newest.GetProperty("level").GetString());
        Assert.Equal("System.IO.IOException", newest.GetProperty("exception_type").GetString());
        Assert.Equal("ComicMaintainer.Core.Services.FileStoreService", newest.GetProperty("source_context").GetString());
        Assert.Equal("Disk full", newest.GetProperty("message").GetString());
        Assert.Equal(7, newest.GetProperty("occurrence_count").GetInt32());
        Assert.Equal(BaseTime.AddDays(-10), newest.GetProperty("first_seen_at").GetDateTime());
        Assert.Equal(BaseTime.AddHours(-1), newest.GetProperty("last_seen_at").GetDateTime());
        Assert.Equal("reported", newest.GetProperty("state").GetString());
        Assert.Equal(321, newest.GetProperty("github_issue_number").GetInt32());
        Assert.Equal("https://github.test/issues/321", newest.GetProperty("github_issue_url").GetString());
        Assert.Equal(BaseTime.AddHours(-2), newest.GetProperty("last_reported_at").GetDateTime());
        Assert.Equal("previous attempt timed out", newest.GetProperty("last_error").GetString());
        // The list view never carries the stack trace; that is detail-only.
        Assert.False(newest.TryGetProperty("stack_trace", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-25)]
    public async Task GetReports_NonPositiveLimitStillReturnsOneReport(int limit)
    {
        await SeedAsync(Report("fp-a", 3), Report("fp-b", 2));

        var json = OkJson(await CreateController().GetReports(limit));

        Assert.Equal(2, json.GetProperty("total").GetInt32());
        Assert.Equal("fp-b", Assert.Single(json.GetProperty("reports").EnumerateArray()).GetProperty("fingerprint").GetString());
    }

    [Fact]
    public async Task GetReports_PageSizeIsCappedAt200()
    {
        await SeedAsync(Enumerable.Range(0, 205).Select(i => Report($"fp-{i:D3}", i)).ToArray());

        var json = OkJson(await CreateController().GetReports(limit: 5000));

        Assert.Equal(205, json.GetProperty("total").GetInt32());
        Assert.Equal(200, json.GetProperty("reports").GetArrayLength());
    }

    [Fact]
    public async Task GetReports_FiltersByStateBeforeCountingAndPaging()
    {
        await SeedAsync(
            Report("fp-new-1", 4),
            Report("fp-muted", 1, r => r.State = ErrorReportState.Muted),
            Report("fp-new-2", 2),
            Report("fp-failed", 3, r => r.State = ErrorReportState.Failed));

        var json = OkJson(await CreateController().GetReports(limit: 1, state: ErrorReportState.New));

        Assert.Equal(2, json.GetProperty("total").GetInt32());
        Assert.Equal("fp-new-2", Assert.Single(json.GetProperty("reports").EnumerateArray()).GetProperty("fingerprint").GetString());
    }

    [Fact]
    public async Task GetReports_BlankStateDoesNotFilter()
    {
        await SeedAsync(Report("fp-a", 1), Report("fp-b", 2, r => r.State = ErrorReportState.Muted));

        var json = OkJson(await CreateController().GetReports(state: "  "));

        Assert.Equal(2, json.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task GetReports_UnknownStateIsBadRequestAndEchoedSafely()
    {
        await SeedAsync(Report("fp-a", 1));

        var result = await CreateController().GetReports(state: "open\r\n\tforged\0");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(@"Unknown state 'open\r\n\tforged\0'", ToJson(badRequest.Value).GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetReports_StateFilterIsCaseSensitive()
    {
        var result = await CreateController().GetReports(state: "Muted");

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ─── Detail ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetReport_ReturnsTheFullDetailOfOneFingerprint()
    {
        await SeedAsync(
            Report("fp-other", 1),
            Report("fp-detail", 2, r =>
            {
                r.Level = "Error";
                r.ExceptionType = "System.InvalidOperationException";
                r.SourceContext = "ComicMaintainer.Core.Services.WatcherService";
                r.MessageTemplate = "Failed to process {Path}";
                r.RenderedMessage = "Failed to process <path>";
                r.StackTrace = "at Watcher.Process()";
                r.AppVersion = "3.2.1";
                r.CorrelationId = "corr-9";
                r.OccurrenceCount = 4;
                r.GitHubIssueNumber = 12;
                r.GitHubIssueUrl = "https://github.test/issues/12";
                r.LastReportedAt = BaseTime.AddHours(-3);
                r.State = ErrorReportState.Reported;
                r.LastError = "none";
            }));

        var json = OkJson(await CreateController().GetReport("fp-detail"));

        Assert.Equal("fp-detail", json.GetProperty("fingerprint").GetString());
        Assert.Equal("Error", json.GetProperty("level").GetString());
        Assert.Equal("System.InvalidOperationException", json.GetProperty("exception_type").GetString());
        Assert.Equal("ComicMaintainer.Core.Services.WatcherService", json.GetProperty("source_context").GetString());
        Assert.Equal("Failed to process {Path}", json.GetProperty("message_template").GetString());
        Assert.Equal("Failed to process <path>", json.GetProperty("message").GetString());
        Assert.Equal("at Watcher.Process()", json.GetProperty("stack_trace").GetString());
        Assert.Equal("3.2.1", json.GetProperty("app_version").GetString());
        Assert.Equal("corr-9", json.GetProperty("correlation_id").GetString());
        Assert.Equal(4, json.GetProperty("occurrence_count").GetInt32());
        Assert.Equal(BaseTime.AddDays(-10), json.GetProperty("first_seen_at").GetDateTime());
        Assert.Equal(BaseTime.AddHours(-2), json.GetProperty("last_seen_at").GetDateTime());
        Assert.Equal("reported", json.GetProperty("state").GetString());
        Assert.Equal(12, json.GetProperty("github_issue_number").GetInt32());
        Assert.Equal("https://github.test/issues/12", json.GetProperty("github_issue_url").GetString());
        Assert.Equal(BaseTime.AddHours(-3), json.GetProperty("last_reported_at").GetDateTime());
        Assert.Equal("none", json.GetProperty("last_error").GetString());
    }

    [Fact]
    public async Task GetReport_UnknownFingerprintIsNotFound()
    {
        await SeedAsync(Report("fp-a", 1));

        var result = await CreateController().GetReport("fp-missing");

        Assert.Equal("Error report not found", ErrorOf<NotFoundObjectResult>(result.Result));
    }

    // ─── Mute / unmute ──────────────────────────────────────────────────────

    [Fact]
    public async Task Mute_PersistsTheMutedState()
    {
        await SeedAsync(Report("fp-noisy", 1), Report("fp-bystander", 2));

        var result = await CreateController().Mute("fp-noisy");

        var json = ToJson(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("Error report updated", json.GetProperty("message").GetString());
        Assert.Equal(ErrorReportState.Muted, json.GetProperty("state").GetString());
        Assert.Equal(ErrorReportState.Muted, await StoredStateAsync("fp-noisy"));
        Assert.Equal(ErrorReportState.New, await StoredStateAsync("fp-bystander"));
    }

    [Theory]
    [InlineData(42, ErrorReportState.Reported)]
    [InlineData(null, ErrorReportState.New)]
    public async Task Unmute_ReturnsToReportedOnlyWhenAnIssueExists(int? issueNumber, string expectedState)
    {
        await SeedAsync(Report("fp-muted", 1, r =>
        {
            r.State = ErrorReportState.Muted;
            r.GitHubIssueNumber = issueNumber;
        }));

        var result = await CreateController().Unmute("fp-muted");

        var json = ToJson(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(expectedState, json.GetProperty("state").GetString());
        Assert.Equal(expectedState, await StoredStateAsync("fp-muted"));
    }

    [Fact]
    public async Task MuteAndUnmute_UnknownFingerprintIsNotFound()
    {
        var controller = CreateController();

        Assert.Equal("Error report not found", ErrorOf<NotFoundObjectResult>(await controller.Mute("fp-missing")));
        Assert.Equal("Error report not found", ErrorOf<NotFoundObjectResult>(await controller.Unmute("fp-missing")));
    }

    // ─── Test report ────────────────────────────────────────────────────────

    [Fact]
    public async Task SendTestReport_BuildsAHarmlessReportTaggedWithTheRequest()
    {
        var report = TestReport();
        _reportFactory
            .Setup(f => f.Create(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Exception?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime>()))
            .Returns(report);
        _reportingService.Setup(s => s.ProcessAsync(report, It.IsAny<CancellationToken>())).ReturnsAsync(ErrorReportOutcome.IssueCreated);
        var before = DateTime.UtcNow;

        await CreateController().SendTestReport();

        _reportFactory.Verify(f => f.Create(
            "Error",
            TestMessage,
            TestMessage,
            null,
            typeof(ErrorReportsController).FullName,
            "trace-77",
            It.Is<DateTime>(t => t.Kind == DateTimeKind.Utc && t >= before && t <= DateTime.UtcNow)), Times.Once);
        _reportingService.Verify(s => s.ProcessAsync(report, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(ErrorReportOutcome.IssueCreated, "Test report delivered to GitHub")]
    [InlineData(ErrorReportOutcome.CommentAdded, "Test report delivered to GitHub")]
    [InlineData(ErrorReportOutcome.Recorded, "Test report was recorded; an issue already exists for it")]
    public async Task SendTestReport_SuccessfulOutcomesAreOk(ErrorReportOutcome outcome, string expectedMessage)
    {
        SetupTestOutcome(outcome);

        var result = await CreateController().SendTestReport();

        var json = ToJson(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(expectedMessage, json.GetProperty("message").GetString());
        Assert.Equal(outcome.ToString(), json.GetProperty("outcome").GetString());
        Assert.Equal("fp-test", json.GetProperty("fingerprint").GetString());
    }

    [Theory]
    [InlineData(ErrorReportOutcome.Disabled, "Error reporting is disabled")]
    [InlineData(ErrorReportOutcome.NotConfigured, "A GitHub token must be configured")]
    [InlineData(ErrorReportOutcome.RateLimited, "The daily issue cap has been reached")]
    [InlineData(ErrorReportOutcome.Muted, "The test report fingerprint is muted")]
    public async Task SendTestReport_OutcomesTheOperatorCanFixAreBadRequests(ErrorReportOutcome outcome, string expectedError)
    {
        SetupTestOutcome(outcome);

        var result = await CreateController().SendTestReport();

        Assert.Equal(expectedError, ErrorOf<BadRequestObjectResult>(result));
    }

    [Fact]
    public async Task SendTestReport_DeliveryFailureIsBadGatewayNamingTheDestination()
    {
        SetupTestOutcome(ErrorReportOutcome.Failed);

        var result = Assert.IsType<ObjectResult>(await CreateController().SendTestReport());

        Assert.Equal(502, result.StatusCode);
        var json = ToJson(result.Value);
        Assert.Equal(
            $"The test report could not be delivered to GitHub; check connectivity and that the token grants issues:write on {ErrorReportingDestination.Slug}",
            json.GetProperty("error").GetString());
        Assert.Equal("Failed", json.GetProperty("outcome").GetString());
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private ErrorReportsController CreateController() =>
        new(_dbContextFactory, _reportFactory.Object, _reportingService.Object, NullLogger<ErrorReportsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { TraceIdentifier = "trace-77" }
            }
        };

    private void SetupTestOutcome(ErrorReportOutcome outcome)
    {
        var report = TestReport();
        _reportFactory
            .Setup(f => f.Create(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Exception?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime>()))
            .Returns(report);
        _reportingService.Setup(s => s.ProcessAsync(report, It.IsAny<CancellationToken>())).ReturnsAsync(outcome);
    }

    private static ErrorReport TestReport() => new()
    {
        Fingerprint = "fp-test",
        Level = "Error",
        MessageTemplate = TestMessage,
        RenderedMessage = TestMessage,
        TimestampUtc = BaseTime
    };

    private static ErrorReportEntity Report(string fingerprint, int lastSeenHoursAgo, Action<ErrorReportEntity>? configure = null)
    {
        var report = new ErrorReportEntity
        {
            Fingerprint = fingerprint,
            Level = "Error",
            MessageTemplate = "Something failed",
            RenderedMessage = "Something failed",
            FirstSeenAt = BaseTime.AddDays(-10),
            LastSeenAt = BaseTime.AddHours(-lastSeenHoursAgo),
            State = ErrorReportState.New
        };
        configure?.Invoke(report);
        return report;
    }

    private async Task SeedAsync(params ErrorReportEntity[] reports)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.ErrorReports.AddRange(reports);
        await db.SaveChangesAsync();
    }

    private async Task<string> StoredStateAsync(string fingerprint)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        return (await db.ErrorReports.AsNoTracking().SingleAsync(e => e.Fingerprint == fingerprint)).State;
    }

    private static JsonElement ToJson(object? value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement OkJson(ActionResult<object> result) =>
        ToJson(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static string? ErrorOf<TResult>(IActionResult? result)
        where TResult : ObjectResult =>
        ToJson(Assert.IsType<TResult>(result).Value).GetProperty("error").GetString();
}
