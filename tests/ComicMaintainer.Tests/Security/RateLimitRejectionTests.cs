using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using ComicMaintainer.WebApi.Authentication;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Security;

/// <summary>
/// What a client gets when the rate limiter refuses it (SEC-3): a message it can show and how long to wait, and the
/// operator is told when an unlisted proxy makes all its clients share one limit.
/// </summary>
public sealed class RateLimitRejectionTests
{
    private static readonly IPAddress Proxy = IPAddress.Parse("172.20.0.5");

    private readonly Mock<ILogger<RateLimitRejection>> _logger = new();

    private static TrustedProxyAllowlist Allowlist(string? proxies)
    {
        Assert.True(TrustedProxyAllowlist.TryParseOptional(proxies, "TRUSTED_PROXIES", out var allowlist, out var error), error);
        return allowlist;
    }

    private DefaultHttpContext NewContext(IPAddress? peer = null, string? forwardedFor = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_logger.Object);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        context.Connection.RemoteIpAddress = peer;
        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        return context;
    }

    private static OnRejectedContext Rejected(HttpContext context, TimeSpan? retryAfter = null) =>
        new() { HttpContext = context, Lease = new FakeLease(retryAfter) };

    private static string ErrorOf(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(context.Response.Body);
        return document.RootElement.GetProperty("error").GetString()!;
    }

    [Theory]
    [InlineData(null, "Too many requests. Please try again shortly.")]
    [InlineData(1, "Too many requests. Please try again in 1 second.")]
    [InlineData(59, "Too many requests. Please try again in 59 seconds.")]
    [InlineData(60, "Too many requests. Please try again in 1 minute.")]
    [InlineData(61, "Too many requests. Please try again in 2 minutes.")]
    [InlineData(300, "Too many requests. Please try again in 5 minutes.")]
    public void DescribesHowLongToWait(int? seconds, string expected)
    {
        Assert.Equal(expected, RateLimitRejection.DescribeWait(seconds));
    }

    [Fact]
    public async Task WritesTheErrorAndRetryAfter()
    {
        var context = NewContext();

        await new RateLimitRejection(Allowlist(null)).WriteAsync(Rejected(context, TimeSpan.FromSeconds(90.2)), CancellationToken.None);

        Assert.Equal("91", context.Response.Headers.RetryAfter.ToString());
        Assert.StartsWith("application/json", context.Response.ContentType);
        Assert.Equal("Too many requests. Please try again in 2 minutes.", ErrorOf(context));
    }

    [Fact]
    public async Task WritesTheErrorWithoutRetryAfterWhenTheLimiterDoesNotSay()
    {
        var context = NewContext();

        await new RateLimitRejection(Allowlist(null)).WriteAsync(Rejected(context), CancellationToken.None);

        Assert.False(context.Response.Headers.ContainsKey("Retry-After"));
        Assert.Equal("Too many requests. Please try again shortly.", ErrorOf(context));
    }

    [Fact]
    public async Task WarnsOnceWhenAnUnlistedProxyMakesItsClientsShareALimit()
    {
        var rejection = new RateLimitRejection(Allowlist(null));

        await rejection.WriteAsync(Rejected(NewContext(Proxy, "203.0.113.7")), CancellationToken.None);
        await rejection.WriteAsync(Rejected(NewContext(Proxy, "203.0.113.8")), CancellationToken.None);

        _logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state!.ToString()!.Contains("TRUSTED_PROXIES") && state.ToString()!.Contains(Proxy.ToString())),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Theory]
    [InlineData(null, "203.0.113.7", true)]    // No peer address to name.
    [InlineData("172.20.0.5", null, true)]     // A direct client, not behind a proxy.
    [InlineData("172.20.0.5", "203.0.113.7", false)]
    public async Task DoesNotWarnWithoutAnUnlistedProxy(string? peer, string? forwardedFor, bool listIsEmpty)
    {
        var context = NewContext(peer is null ? null : IPAddress.Parse(peer), forwardedFor);

        await new RateLimitRejection(Allowlist(listIsEmpty ? null : "172.20.0.0/16")).WriteAsync(Rejected(context), CancellationToken.None);

        _logger.VerifyNoOtherCalls();
    }

    private sealed class FakeLease(TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames =>
            retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = retryAfter;
            return retryAfter is not null && metadataName == MetadataName.RetryAfter.Name;
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
