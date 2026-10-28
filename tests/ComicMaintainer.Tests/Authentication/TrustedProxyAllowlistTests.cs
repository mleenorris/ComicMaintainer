using System.Net;
using ComicMaintainer.WebApi.Authentication;

namespace ComicMaintainer.Tests.Authentication;

/// <summary>
/// <see cref="TrustedProxyAllowlist"/> as used for <c>TRUSTED_PROXIES</c>: an optional list (empty trusts nobody)
/// that is combined with <c>AUTHELIA_TRUSTED_PROXIES</c>. The entry format itself is covered by
/// <see cref="AutheliaTrustedProxyTests"/>.
/// </summary>
public sealed class TrustedProxyAllowlistTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ,")]
    public void TryParseOptional_AcceptsAMissingOrEmptyListThatTrustsNobody(string? value)
    {
        Assert.True(TrustedProxyAllowlist.TryParseOptional(value, "TRUSTED_PROXIES", out var allowlist, out var error));

        Assert.Null(error);
        Assert.Empty(allowlist.Networks);
        Assert.False(allowlist.Contains(IPAddress.Parse("172.18.0.2")));
        Assert.False(allowlist.Contains(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void TryParseOptional_ParsesTheSameEntriesAsTryParse()
    {
        Assert.True(TrustedProxyAllowlist.TryParseOptional(
            " 172.18.0.0/16 ,10.0.0.5,\tfd00::/8, ::ffff:172.30.0.0/120", "TRUSTED_PROXIES", out var allowlist, out _));

        Assert.Equal(
            ["172.18.0.0/16", "10.0.0.5/32", "fd00::/8", "172.30.0.0/24"],
            allowlist.Networks.Select(network => network.ToString()));
    }

    [Theory]
    [InlineData("swag")]
    [InlineData("172.18.0.1/16")]
    [InlineData("10")]
    [InlineData("10.0.0.5 10.0.0.6")]
    public void TryParseOptional_RejectsAnInvalidEntryAndNamesTheSetting(string entry)
    {
        Assert.False(TrustedProxyAllowlist.TryParseOptional("10.0.0.5, " + entry, "TRUSTED_PROXIES", out var allowlist, out var error));

        Assert.Null(allowlist);
        Assert.StartsWith($"Invalid TRUSTED_PROXIES entry '{entry}'", error);
        Assert.DoesNotContain("AUTHELIA", error);
    }

    [Fact]
    public void Union_TrustsTheAddressesOfBothListsOnce()
    {
        var reverseProxies = Parse("172.18.0.0/16, 10.0.0.5");
        var autheliaProxies = Parse("10.0.0.5, fd00::/8");

        var union = reverseProxies.Union(autheliaProxies);

        Assert.Equal(["172.18.0.0/16", "10.0.0.5/32", "fd00::/8"], union.Networks.Select(network => network.ToString()));
        Assert.True(union.Contains(IPAddress.Parse("172.18.0.2")));
        Assert.True(union.Contains(IPAddress.Parse("fd00::2")));
        Assert.False(union.Contains(IPAddress.Parse("10.0.0.6")));
        // The lists themselves are unchanged.
        Assert.False(reverseProxies.Contains(IPAddress.Parse("fd00::2")));
        Assert.False(autheliaProxies.Contains(IPAddress.Parse("172.18.0.2")));
    }

    [Fact]
    public void Union_WithAnEmptyList_TrustsTheSameAddresses()
    {
        var union = Parse("").Union(Parse("172.18.0.0/16"));

        Assert.Equal(["172.18.0.0/16"], union.Networks.Select(network => network.ToString()));
    }

    private static TrustedProxyAllowlist Parse(string value)
    {
        Assert.True(TrustedProxyAllowlist.TryParseOptional(value, "TRUSTED_PROXIES", out var allowlist, out var error), error);
        return allowlist;
    }
}
