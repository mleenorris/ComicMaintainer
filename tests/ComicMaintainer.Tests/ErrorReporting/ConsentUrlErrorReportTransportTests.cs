using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.ErrorReporting.Models;
using ComicMaintainer.Core.ErrorReporting.Services;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.ErrorReporting;

/// <summary>
/// The consent transport is the default, so it is what almost every install
/// will actually use. It must transmit nothing, produce a URL a browser will
/// accept, and never be steerable to a repository other than the configured
/// one.
/// </summary>
public class ConsentUrlErrorReportTransportTests
{
    private static ErrorReport Report(string? message = "boom", int stackFrames = 3)
    {
        var frames = string.Join("\n", Enumerable.Range(0, stackFrames)
            .Select(i => $"   at ComicMaintainer.Core.Services.Service{i}.Method{i}()"));

        return new ErrorReport
        {
            Fingerprint = "a1b2c3d4e5f60718",
            ExceptionType = "System.NullReferenceException",
            Message = message ?? string.Empty,
            StackTrace = frames,
            Origin = "GET /api/files",
            Source = ErrorReportSource.Api,
            Area = "core",
            AppVersion = "2.0.310",
            Platform = ".NET 10.0.0 / Linux",
            OccurrenceCount = 1,
            FirstSeenUtc = DateTime.UnixEpoch,
            LastSeenUtc = DateTime.UnixEpoch,
        };
    }

    private static ConsentUrlErrorReportTransport CreateTransport(
        string? repository = "mleenorris/ComicMaintainer")
    {
        var settings = new AppSettings
        {
            GitHubRepository = repository!,
        };

        var monitor = new Mock<IOptionsMonitor<AppSettings>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(settings);

        return new ConsentUrlErrorReportTransport(monitor.Object);
    }

    [Fact]
    public async Task SendAsync_TransmitsNothing()
    {
        var result = await CreateTransport().SendAsync(Report());

        Assert.False(result.Delivered);
        Assert.Null(result.IssueNumber);
        Assert.NotNull(result.IssueUrl);
    }

    [Fact]
    public async Task SendAsync_TargetsTheConfiguredRepositoryAndTemplate()
    {
        var result = await CreateTransport().SendAsync(Report());

        Assert.StartsWith(
            "https://github.com/mleenorris/ComicMaintainer/issues/new?",
            result.IssueUrl,
            StringComparison.Ordinal);
        Assert.Contains("template=auto_error_report.yml", result.IssueUrl!, StringComparison.Ordinal);
    }

    [Theory]
    // A malformed or hostile setting must not be able to send the user's
    // browser — carrying their report — to an arbitrary host.
    [InlineData("evil.example.com/owner/repo")]
    [InlineData("https://evil.example.com/o/r")]
    [InlineData("owner/repo/extra")]
    [InlineData("owner")]
    [InlineData("../../etc/passwd")]
    [InlineData("")]
    [InlineData(null)]
    public async Task SendAsync_RejectsUnsafeRepositorySettings(string? repository)
    {
        var result = await CreateTransport(repository).SendAsync(Report());

        Assert.StartsWith(
            "https://github.com/mleenorris/ComicMaintainer/issues/new?",
            result.IssueUrl,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_TruncatesLongReportsToStayWithinUrlLimits()
    {
        // GitHub (and some browsers) reject very long URLs outright, which
        // would leave the user with a blank page instead of a report.
        var result = await CreateTransport().SendAsync(Report(
            message: new string('x', 20_000),
            stackFrames: 400));

        Assert.NotNull(result.IssueUrl);
        Assert.True(
            result.IssueUrl!.Length < 8000,
            $"Prefill URL was {result.IssueUrl.Length} characters, which browsers may reject.");
    }

    [Fact]
    public void BuildIssueUrl_PercentEncodesTheBody()
    {
        // Unencoded '&' or '#' would silently truncate the report at that
        // character, and the maintainer would never know what was missing.
        var url = ConsentUrlErrorReportTransport.BuildIssueUrl(
            "mleenorris/ComicMaintainer",
            Report(message: "a&b#c d=e"));

        Assert.DoesNotContain("a&b#c", url, StringComparison.Ordinal);
        Assert.Contains("a%26b%23c", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrefilledBody_KeepsTheIssueStructuredWhenTruncated()
    {
        // Cutting the rendered body at a fixed character count can end inside
        // a code fence, so the fingerprint marker and the facts below it are
        // swallowed by the fence and the maintainer sees a broken issue.
        var body = ConsentUrlErrorReportTransport.BuildPrefilledBody(Report(
            message: new string('x', 20_000),
            stackFrames: 400));

        var fenceCount = body.Split("```").Length - 1;
        Assert.True(fenceCount % 2 == 0, $"Body ended inside a code fence ({fenceCount} fences).");
        Assert.Contains("a1b2c3d4e5f60718", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildIssueUrl_IsAWellFormedAbsoluteUri()
    {
        var url = ConsentUrlErrorReportTransport.BuildIssueUrl("mleenorris/ComicMaintainer", Report());

        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var parsed));
        Assert.Equal(Uri.UriSchemeHttps, parsed!.Scheme);
        Assert.Equal("github.com", parsed.Host);
    }
}
