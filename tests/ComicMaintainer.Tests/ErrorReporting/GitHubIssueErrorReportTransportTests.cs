using System.Net;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.ErrorReporting.Models;
using ComicMaintainer.Core.ErrorReporting.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.ErrorReporting;

/// <summary>
/// The automatic transport writes to a public issue tracker with the owner's
/// token, so its failure behaviour matters as much as its success behaviour:
/// it must never turn a transient GitHub error into duplicate public issues.
/// </summary>
public class GitHubIssueErrorReportTransportTests
{
    private static ErrorReport Report() => new()
    {
        Fingerprint = "a1b2c3d4e5f60718",
        ExceptionType = "System.NullReferenceException",
        Message = "boom",
        StackTrace = "   at ComicMaintainer.Core.Services.Service.Method()",
        Origin = "GET /api/files",
        Source = ErrorReportSource.Api,
        Area = "core",
        AppVersion = "2.0.310",
        Platform = ".NET 10.0.0 / Linux",
        OccurrenceCount = 1,
        FirstSeenUtc = DateTime.UnixEpoch,
        LastSeenUtc = DateTime.UnixEpoch,
    };

    private static GitHubIssueErrorReportTransport CreateTransport(StubHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(f => f.CreateClient(GitHubIssueErrorReportTransport.HttpClientName))
            .Returns(() => new HttpClient(handler, disposeHandler: false)
            {
                // Program.cs configures the base address on the named client;
                // the transport issues relative request URIs.
                BaseAddress = new Uri("https://api.github.com/"),
            });

        var settings = new AppSettings
        {
            GitHubRepository = "mleenorris/ComicMaintainer",
            GitHubToken = "token-value",
        };

        var monitor = new Mock<IOptionsMonitor<AppSettings>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(settings);

        return new GitHubIssueErrorReportTransport(
            factory.Object,
            monitor.Object,
            new Mock<ILogger<GitHubIssueErrorReportTransport>>().Object);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task SendAsync_DoesNotCreateAnIssueWhenTheSearchFails(HttpStatusCode status)
    {
        // A failed search is not evidence that no issue exists. Treating it as
        // "none found" opens a duplicate of an issue that is already open, and
        // rate limiting is exactly when that happens repeatedly.
        var handler = new StubHandler(status);

        var result = await CreateTransport(handler).SendAsync(Report());

        Assert.False(result.Delivered);
        Assert.Null(result.IssueNumber);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _searchStatus;

        public StubHandler(HttpStatusCode searchStatus) => _searchStatus = searchStatus;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);

            return Task.FromResult(new HttpResponseMessage(_searchStatus)
            {
                Content = new StringContent("{}"),
            });
        }
    }
}
