using System.Net;
using ComicMaintainer.WebApi.Authentication;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace ComicMaintainer.Tests.Infrastructure;

/// <summary>
/// <see cref="ForwardedClientAddress"/>: the client address the rate limiter counts anonymous requests by. Behind
/// trusted proxies it is the right-most <c>X-Forwarded-For</c> entry that is not a trusted proxy; every other
/// request is identified by its TCP peer, whatever <c>X-Forwarded-For</c> it sends.
/// </summary>
public sealed class ForwardedClientAddressTests
{
    private static readonly TrustedProxyAllowlist TrustedProxies = Allowlist("172.18.0.0/16, 10.0.0.5, fd00::/8");

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8::7", "2001:db8::7")]
    [InlineData("203.0.113.7:51234", "203.0.113.7")]           // some proxies append the client's port
    [InlineData("[2001:db8::7]:51234", "2001:db8::7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]          // one client, one partition, however it is written
    [InlineData(" 203.0.113.7 ", "203.0.113.7")]
    public void TrustedProxy_TheClientItForwardedIsUsed(string forwardedFor, string expected)
    {
        Assert.Equal(IPAddress.Parse(expected), Resolve("172.18.0.2", forwardedFor));
    }

    [Theory]
    [InlineData("172.18.0.2")]
    [InlineData("172.18.255.254")]
    [InlineData("10.0.0.5")]
    [InlineData("fd00::2")]
    public void EveryTrustedProxy_HasItsForwardedClientUsed(string peer)
    {
        Assert.Equal(IPAddress.Parse("203.0.113.7"), Resolve(peer, "203.0.113.7"));
    }

    [Theory]
    [InlineData("198.51.100.1, 203.0.113.7")]                  // the client sent its own X-Forwarded-For
    [InlineData("198.51.100.1,203.0.113.7")]
    [InlineData("unknown, 172.18.0.9, 203.0.113.7")]
    public void TrustedProxy_EntriesTheClientWroteAreIgnored(string forwardedFor)
    {
        Assert.Equal(IPAddress.Parse("203.0.113.7"), Resolve("172.18.0.2", forwardedFor));
    }

    [Theory]
    [InlineData("198.51.100.1, 203.0.113.7, 10.0.0.5")]       // client → proxy 10.0.0.5 → proxy 172.18.0.2 → app
    [InlineData("198.51.100.1, 203.0.113.7, fd00::5, 10.0.0.5")]
    [InlineData("203.0.113.7, , 10.0.0.5,")]                    // empty entries are skipped
    public void ChainOfTrustedProxies_IsFollowedToTheFirstUntrustedAddress(string forwardedFor)
    {
        Assert.Equal(IPAddress.Parse("203.0.113.7"), Resolve("172.18.0.2", forwardedFor));
    }

    [Fact]
    public void ForwardedForSentAsSeveralHeaderLines_IsReadAsOneList()
    {
        var context = Request("172.18.0.2");
        context.Request.Headers["X-Forwarded-For"] = new[] { "198.51.100.1", "203.0.113.7, 10.0.0.5" };

        Assert.Equal(IPAddress.Parse("203.0.113.7"), ForwardedClientAddress.Resolve(context, TrustedProxies));
    }

    [Fact]
    public void EveryForwardedAddressTrusted_TheLeftMostIsUsed()
    {
        Assert.Equal(IPAddress.Parse("172.18.0.40"), Resolve("172.18.0.2", "172.18.0.40, 10.0.0.5"));
    }

    [Theory]
    [InlineData("unknown", "172.18.0.2")]
    [InlineData("198.51.100.1, _hidden", "172.18.0.2")]
    [InlineData("203.0.113.7, unknown, 10.0.0.5", "10.0.0.5")]
    [InlineData("203.0.113.7, 10.0.0.5:99999", "172.18.0.2")]
    public void UnparsableEntry_ThePeerThatWroteItIsUsed(string forwardedFor, string expected)
    {
        Assert.Equal(IPAddress.Parse(expected), Resolve("172.18.0.2", forwardedFor));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , ")]
    public void TrustedProxyWithoutForwardedFor_IsTheClient(string? forwardedFor)
    {
        Assert.Equal(IPAddress.Parse("172.18.0.2"), Resolve("172.18.0.2", forwardedFor));
    }

    [Theory]
    [InlineData("192.0.2.44")]                                  // a client that bypasses the proxy
    [InlineData("172.19.0.2")]
    [InlineData("10.0.0.6")]
    [InlineData("2001:db8::44")]
    public void UntrustedPeer_ForwardedForIsIgnored(string peer)
    {
        Assert.Equal(IPAddress.Parse(peer), Resolve(peer, "203.0.113.7"));
    }

    [Fact]
    public void NoTrustedProxies_ForwardedForIsIgnored()
    {
        var context = Request("172.18.0.2", "203.0.113.7");

        Assert.Equal(IPAddress.Parse("172.18.0.2"), ForwardedClientAddress.Resolve(context, Allowlist("")));
    }

    [Theory]
    [InlineData("::ffff:172.18.0.2", "203.0.113.7", "203.0.113.7")]   // a dual-mode socket's IPv4 peer
    [InlineData("::ffff:192.0.2.44", "203.0.113.7", "192.0.2.44")]
    public void IPv4MappedPeer_IsMatchedAndReportedAsIPv4(string peer, string forwardedFor, string expected)
    {
        Assert.Equal(IPAddress.Parse(expected), Resolve(peer, forwardedFor));
    }

    [Fact]
    public void UnknownPeer_IsNull()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.7";

        Assert.Null(ForwardedClientAddress.Resolve(context, TrustedProxies));
    }

    [Fact]
    public void ResolvingTheAddress_LeavesTheConnectionUntouched()
    {
        var context = Request("172.18.0.2", "203.0.113.7");

        ForwardedClientAddress.Resolve(context, TrustedProxies);

        Assert.Equal(IPAddress.Parse("172.18.0.2"), context.Connection.RemoteIpAddress);
    }

    private static IPAddress? Resolve(string peer, string? forwardedFor) =>
        ForwardedClientAddress.Resolve(Request(peer, forwardedFor), TrustedProxies);

    private static DefaultHttpContext Request(string peer, string? forwardedFor = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        return context;
    }

    private static TrustedProxyAllowlist Allowlist(string value)
    {
        Assert.True(TrustedProxyAllowlist.TryParseOptional(value, "TRUSTED_PROXIES", out var allowlist, out var error), error);
        return allowlist;
    }
}
