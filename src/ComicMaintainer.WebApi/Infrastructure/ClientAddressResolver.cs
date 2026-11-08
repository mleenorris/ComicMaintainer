using System.Net;
using ComicMaintainer.WebApi.Authentication;

namespace ComicMaintainer.WebApi.Infrastructure;

/// <summary>
/// Resolves the address of the client behind a request with the reverse proxies trusted by <c>TRUSTED_PROXIES</c>
/// and <c>TRUSTED_PROXY_HOPS</c> (see <see cref="ForwardedClientAddress"/>), the same way the rate limiter does, so
/// that sign-in logs and limits name the client and not the proxy.
/// </summary>
public sealed class ClientAddressResolver
{
    private readonly TrustedProxyAllowlist _trustedProxies;
    private readonly int _forwardedHopLimit;

    public ClientAddressResolver(TrustedProxyAllowlist trustedProxies, int forwardedHopLimit = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(forwardedHopLimit);
        _trustedProxies = trustedProxies;
        _forwardedHopLimit = forwardedHopLimit;
    }

    /// <summary>A resolver that trusts no proxy: the client is the TCP peer.</summary>
    public static ClientAddressResolver Direct { get; } = new(TrustedProxyAllowlist.TryParseOptional(null, "TRUSTED_PROXIES", out var none, out _)
        ? none
        : throw new InvalidOperationException("An empty proxy list must parse."));

    /// <summary>The client's address, or <see langword="null"/> when the server does not know the peer.</summary>
    public IPAddress? Resolve(HttpContext context) => ForwardedClientAddress.Resolve(context, _trustedProxies, _forwardedHopLimit);

    /// <summary>The client's address for log messages: <c>unknown</c> when the server does not know the peer.</summary>
    public string Describe(HttpContext? context) => (context is null ? null : Resolve(context))?.ToString() ?? "unknown";
}
