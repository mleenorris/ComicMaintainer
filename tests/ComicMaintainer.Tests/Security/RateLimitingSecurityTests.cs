using System.Net;
using System.Threading.RateLimiting;
using ComicMaintainer.WebApi.Startup;
using ComicMaintainer.Tests.Startup;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Tests.Security;

/// <summary>
/// The partitions of the global rate limiter behind a reverse proxy. Every request reaches the app from the proxy,
/// so anonymous requests relayed by a trusted proxy (<c>TRUSTED_PROXIES</c>, plus <c>AUTHELIA_TRUSTED_PROXIES</c>
/// when Authelia is enabled) are counted per client address in <c>X-Forwarded-For</c>: one client exhausting its
/// anonymous limit must not make signing in return 429 for everybody else, and no client may escape its own limit
/// by sending the header. The registration reads the process environment, so these tests run in the non-parallel
/// environment collection with the variables cleared.
/// </summary>
[Collection(AppSettingsEnvironmentPostConfigureTests.EnvironmentCollection.Name)]
public sealed class RateLimitingSecurityTests : IDisposable
{
    // Anonymous requests allowed per partition and minute. AttemptAcquire never waits in the limiter's queue.
    private const int AnonymousPermitLimit = 100;

    private const string ClientA = "203.0.113.10";
    private const string ClientB = "198.51.100.20";

    private static readonly IPAddress Proxy = IPAddress.Parse("172.18.0.2");

    private readonly AppSettingsEnvironmentPostConfigureTests.EnvironmentScope _environment =
        new("TRUSTED_PROXIES", "TRUSTED_PROXY_HOPS", "AUTHELIA_ENABLED", "AUTHELIA_TRUSTED_PROXIES");

    public void Dispose() => _environment.Dispose();

    [Fact]
    public void BehindATrustedProxy_OneClientExhaustingItsLimit_DoesNotLimitTheOthers()
    {
        _environment.Set("TRUSTED_PROXIES", "172.18.0.0/16");
        using var limiter = CreateGlobalLimiter();

        ExhaustAnonymousLimit(limiter, () => Anonymous(Proxy, ClientA));

        Assert.True(TryAcquire(limiter, Anonymous(Proxy, ClientB)));
        Assert.False(TryAcquire(limiter, Anonymous(Proxy, ClientA)));
    }

    [Fact]
    public void BehindATrustedProxy_ForgedForwardedForEntriesDoNotEscapeTheLimit()
    {
        _environment.Set("TRUSTED_PROXIES", "172.18.0.0/16");
        using var limiter = CreateGlobalLimiter();

        // The proxy appends the address it received the request from to the X-Forwarded-For the client sent.
        ExhaustAnonymousLimit(limiter, () => Anonymous(Proxy, "192.0.2.1, " + ClientA));

        Assert.False(TryAcquire(limiter, Anonymous(Proxy, "192.0.2.2, " + ClientA)));
        Assert.False(TryAcquire(limiter, Anonymous(Proxy, ClientB + ", " + ClientA)));
        Assert.False(TryAcquire(limiter, Anonymous(Proxy, "172.18.0.3, " + ClientA)));
    }

    [Fact]
    public void DefaultHopLimit_DoesNotFollowPastATrustedGateway()
    {
        _environment.Set("TRUSTED_PROXIES", "172.18.0.0/16");

        Assert.False(GatewayClientsHaveIndependentLimits());
    }

    [Fact]
    public void AdditionalTrustedHops_MustBeExplicitlyConfigured()
    {
        _environment.Set("TRUSTED_PROXIES", "172.18.0.0/16");
        _environment.Set("TRUSTED_PROXY_HOPS", "2");

        Assert.True(GatewayClientsHaveIndependentLimits());
    }

    [Fact]
    public void ForwardedHopLimit_CanBeConfiguredInApplicationSettings()
    {
        _environment.Set("TRUSTED_PROXIES", "172.18.0.0/16");

        Assert.True(GatewayClientsHaveIndependentLimits(("ReverseProxySettings:ForwardedHopLimit", "2")));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("two")]
    public void InvalidForwardedHopLimit_RefusesToStart(string value)
    {
        _environment.Set("TRUSTED_PROXY_HOPS", value);

        var exception = Assert.Throws<InvalidOperationException>(() => CreateGlobalLimiter());

        Assert.StartsWith($"Invalid TRUSTED_PROXY_HOPS value '{value}'", exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("172.18.0.0/16")]
    public void ClientNotRelayedByATrustedProxy_CannotEscapeTheLimitWithForwardedFor(string? trustedProxies)
    {
        _environment.Set("TRUSTED_PROXIES", trustedProxies);
        using var limiter = CreateGlobalLimiter();
        var client = IPAddress.Parse("192.0.2.44");

        ExhaustAnonymousLimit(limiter, () => Anonymous(client, ClientA));

        Assert.False(TryAcquire(limiter, Anonymous(client, ClientB)));
        Assert.False(TryAcquire(limiter, Anonymous(client, "172.18.0.2")));
        Assert.False(TryAcquire(limiter, Anonymous(client)));
    }

    [Fact]
    public void WithoutTrustedProxies_ClientsBehindAProxyShareItsLimit()
    {
        Assert.False(CountsForwardedClientsSeparately(Proxy));
    }

    [Theory]
    [InlineData("172.18.0.0/16", "172.18.0.2")]
    [InlineData("172.18.0.0/16", "::ffff:172.18.0.2")]   // an IPv4 peer of a dual-mode socket
    [InlineData("10.0.0.5, fd00::/8", "fd00::2")]
    [InlineData("10.0.0.5, fd00::/8", "10.0.0.5")]
    public void TrustedProxies_CountEachForwardedClientSeparately(string trustedProxies, string proxy)
    {
        _environment.Set("TRUSTED_PROXIES", trustedProxies);

        Assert.True(CountsForwardedClientsSeparately(IPAddress.Parse(proxy)));
    }

    [Fact]
    public void TrustedProxies_AreReadFromTheConfiguration()
    {
        Assert.True(CountsForwardedClientsSeparately(Proxy, ("ReverseProxySettings:TrustedProxies", "172.18.0.0/16")));
    }

    [Theory]
    [InlineData("172.18.0.0/16", "172.18.0.2", true)]
    [InlineData("172.18.0.0/16", "10.0.0.2", false)]     // the variable replaces the configured list
    [InlineData("", "10.0.0.2", true)]                    // an empty variable keeps the configured list
    [InlineData("", "172.18.0.2", false)]
    [InlineData("   ", "10.0.0.2", true)]
    [InlineData(" \t ", "172.18.0.2", false)]
    public void TrustedProxiesEnvironmentVariable_OverridesTheConfiguration(string environmentValue, string proxy, bool expected)
    {
        _environment.Set("TRUSTED_PROXIES", environmentValue);

        Assert.Equal(expected, CountsForwardedClientsSeparately(
            IPAddress.Parse(proxy), ("ReverseProxySettings:TrustedProxies", "10.0.0.0/8")));
    }

    [Fact]
    public void AutheliaEnabled_ItsTrustedProxiesAreTrustedWithoutTrustedProxies()
    {
        _environment.Set("AUTHELIA_ENABLED", "true");
        _environment.Set("AUTHELIA_TRUSTED_PROXIES", "172.18.0.0/16");

        Assert.True(CountsForwardedClientsSeparately(Proxy));
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.18.0.2")]
    public void AutheliaEnabled_BothListsAreTrusted(string proxy)
    {
        _environment.Set("TRUSTED_PROXIES", "10.0.0.5");
        (string, string?)[] settings = [("AutheliaSettings:Enabled", "true"), ("AutheliaSettings:TrustedProxies", "172.18.0.0/16")];

        Assert.True(CountsForwardedClientsSeparately(IPAddress.Parse(proxy), settings));
    }

    [Fact]
    public void AutheliaDisabled_ItsTrustedProxiesAreNotTrusted()
    {
        _environment.Set("AUTHELIA_TRUSTED_PROXIES", "172.18.0.0/16");

        Assert.False(CountsForwardedClientsSeparately(Proxy, ("AutheliaSettings:Enabled", "false")));
    }

    [Theory]
    [InlineData("swag")]                  // hostnames are not resolved
    [InlineData("172.18.0.1/16")]         // a range must be written with its network address
    public void InvalidTrustedProxiesEntry_RefusesToStartAndNamesTheEntry(string entry)
    {
        _environment.Set("TRUSTED_PROXIES", "10.0.0.5, " + entry);

        var exception = Assert.Throws<InvalidOperationException>(() => CreateGlobalLimiter());

        Assert.StartsWith($"Invalid TRUSTED_PROXIES entry '{entry}'", exception.Message);
        Assert.EndsWith("See docs/REVERSE_PROXY.md.", exception.Message);
    }

    [Fact]
    public void InvalidTrustedProxiesSetting_RefusesToStart()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateGlobalLimiter(("ReverseProxySettings:TrustedProxies", "swag")));

        Assert.StartsWith("Invalid TRUSTED_PROXIES entry 'swag'", exception.Message);
    }

    /// <summary>
    /// Whether client B, behind <paramref name="proxy"/>, can still make an anonymous request once client A, behind
    /// the same proxy, has exhausted its limit.
    /// </summary>
    private static bool CountsForwardedClientsSeparately(IPAddress proxy, params (string Key, string? Value)[] settings)
    {
        using var limiter = CreateGlobalLimiter(settings);

        ExhaustAnonymousLimit(limiter, () => Anonymous(proxy, ClientA));

        return TryAcquire(limiter, Anonymous(proxy, ClientB));
    }

    private static bool GatewayClientsHaveIndependentLimits(params (string Key, string? Value)[] settings)
    {
        using var limiter = CreateGlobalLimiter(settings);
        ExhaustAnonymousLimit(limiter, () => Anonymous(Proxy, ClientA + ", 172.18.0.1"));

        return TryAcquire(limiter, Anonymous(Proxy, ClientB + ", 172.18.0.1"));
    }

    private static PartitionedRateLimiter<HttpContext> CreateGlobalLimiter(params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddComicMaintainerWeb(configuration);
        using var provider = services.BuildServiceProvider();
        return Assert.IsAssignableFrom<PartitionedRateLimiter<HttpContext>>(
            provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter);
    }

    private static void ExhaustAnonymousLimit(PartitionedRateLimiter<HttpContext> limiter, Func<HttpContext> request)
    {
        for (var i = 1; i <= AnonymousPermitLimit; i++)
        {
            Assert.True(TryAcquire(limiter, request()), $"Anonymous request {i} was rejected.");
        }

        Assert.False(TryAcquire(limiter, request()));
    }

    private static bool TryAcquire(PartitionedRateLimiter<HttpContext> limiter, HttpContext request)
    {
        using var lease = limiter.AttemptAcquire(request);
        return lease.IsAcquired;
    }

    private static DefaultHttpContext Anonymous(IPAddress peer, string? forwardedFor = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = peer;
        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        return context;
    }
}
