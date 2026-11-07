using System.Net;
using System.Net.Sockets;
using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

/// <summary>
/// Tests for <see cref="PublicNetworkGuard"/>, which keeps series-image downloads on the public internet (SSRF).
/// </summary>
public class PublicNetworkGuardTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.0.0.8")]
    [InlineData("192.0.2.10")]
    [InlineData("192.88.99.1")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.254")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::127.0.0.1")]
    [InlineData("::8.8.8.8")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("64:ff9b:1::808:808")]
    [InlineData("100::1")]
    [InlineData("2001::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:c0a8:101::1")]
    [InlineData("2002:7f00:1::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData("fe80::1%3")]
    [InlineData("fec0::1")]
    [InlineData("ff02::1")]
    public void IsBlockedAddress_RejectsAddressesOutsideThePublicInternet(string address)
    {
        Assert.True(PublicNetworkGuard.IsBlockedAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("192.0.1.1")]
    [InlineData("192.1.2.3")]
    [InlineData("192.169.0.1")]
    [InlineData("198.17.255.255")]
    [InlineData("198.20.0.1")]
    [InlineData("203.0.114.1")]
    [InlineData("223.255.255.1")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2002:808:808::1")]
    public void IsBlockedAddress_AllowsPublicAddresses(string address)
    {
        Assert.False(PublicNetworkGuard.IsBlockedAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public void CreateHandler_FollowsNoRedirectsUsesNoProxyAndChecksTheAddressWhenConnecting()
    {
        using var handler = PublicNetworkGuard.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.NotNull(handler.ConnectCallback);
    }

    // DNS rebinding: whatever the host resolved to when the URL was checked, the handler resolves it again itself
    // when it connects and refuses a non-public answer, so the request never reaches the internal service.
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task Handler_RefusesToConnectToAHostThatNowResolvesToANonPublicAddress(string rebound)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var lookups = new List<string>();
        using var client = new HttpClient(PublicNetworkGuard.CreateHandler((host, _) =>
        {
            lookups.Add(host);
            return Task.FromResult(new[] { IPAddress.Parse(rebound) });
        }));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://rebind.example:{port}/x.png"));

        Assert.Equal(["rebind.example"], lookups);
        Assert.Contains(Chain(ex), e => e is NonPublicAddressException);
        Assert.False(listener.Pending(), "the handler connected to the internal address");
    }

    [Theory]
    [InlineData("http://127.0.0.1:{0}/x.png")]
    [InlineData("http://[::1]:{0}/x.png")]
    [InlineData("http://localhost:{0}/x.png")]
    public async Task Handler_RefusesLoopbackLiteralsAndNames(string urlFormat)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new HttpClient(PublicNetworkGuard.CreateHandler());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(string.Format(urlFormat, port)));

        Assert.Contains(Chain(ex), e => e is NonPublicAddressException);
        Assert.False(listener.Pending(), "the handler connected to the loopback address");
    }

    [Fact]
    public async Task ResolvePublicAddressesAsync_RejectsAHostWithAnyNonPublicAddress()
    {
        var ex = await Assert.ThrowsAsync<NonPublicAddressException>(() => PublicNetworkGuard.ResolvePublicAddressesAsync(
            "mixed.example",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.5") }),
            CancellationToken.None));

        Assert.Equal("Host 'mixed.example' resolves to a non-public address", ex.Message);
    }

    [Fact]
    public async Task ResolvePublicAddressesAsync_ReturnsTheCheckedPublicAddresses()
    {
        var addresses = await PublicNetworkGuard.ResolvePublicAddressesAsync(
            "cdn.example",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2001:4860:4860::8888") }),
            CancellationToken.None);

        Assert.Equal([IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2001:4860:4860::8888")], addresses);
    }

    [Fact]
    public async Task ResolvePublicAddressesAsync_ReportsAHostThatResolvesToNothing()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => PublicNetworkGuard.ResolvePublicAddressesAsync(
            "empty.example", (_, _) => Task.FromResult(Array.Empty<IPAddress>()), CancellationToken.None));

        Assert.IsNotType<NonPublicAddressException>(ex);
        Assert.Equal("Host 'empty.example' did not resolve to any address", ex.Message);
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }
}
