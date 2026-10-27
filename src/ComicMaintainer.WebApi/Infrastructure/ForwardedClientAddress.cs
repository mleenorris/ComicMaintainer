using System.Net;
using ComicMaintainer.WebApi.Authentication;
using Microsoft.Extensions.Primitives;

namespace ComicMaintainer.WebApi.Infrastructure;

/// <summary>
/// The address of the client that sent a request, as vouched for by the trusted reverse proxies in front of the
/// app. The rate limiter counts anonymous requests per client with it.
/// </summary>
/// <remarks>
/// <para>Behind a reverse proxy every connection comes from the proxy, so keying on the TCP peer would put every
/// visitor in one rate-limit partition, and a single client could lock everybody out of signing in. A proxy appends
/// the address it received the request from to <c>X-Forwarded-For</c>, so the header is read from the right: while
/// the current address is a trusted proxy, the entry that proxy appended replaces it. The first address that is not
/// a trusted proxy is the client. The entries to its left were written by the client (or by a proxy nobody
/// trusts) and are ignored, so a client cannot choose its own address by sending the header.</para>
/// <para>This deliberately leaves <see cref="ConnectionInfo.RemoteIpAddress"/> untouched (no forwarded-headers
/// middleware): the Authelia allowlist must keep checking the TCP peer, see
/// <see cref="AutheliaAuthenticationHandler"/>.</para>
/// </remarks>
public static class ForwardedClientAddress
{
    private const string ForwardedForHeader = "X-Forwarded-For";

    /// <summary>
    /// The client address of <paramref name="context"/>: the right-most <c>X-Forwarded-For</c> entry that is not
    /// in <paramref name="trustedProxies"/> when the TCP peer is, otherwise the TCP peer itself.
    /// </summary>
    /// <returns>
    /// The address, with IPv4-mapped IPv6 addresses converted to IPv4; <see langword="null"/> when the server does
    /// not know the peer.
    /// </returns>
    public static IPAddress? Resolve(HttpContext context, TrustedProxyAllowlist trustedProxies)
    {
        var peer = context.Connection.RemoteIpAddress;
        if (peer is null)
        {
            return null;
        }

        var address = TrustedProxyAllowlist.Normalize(peer);
        if (!trustedProxies.Contains(address))
        {
            // Not relayed by a trusted proxy: X-Forwarded-For is just another header the client controls.
            return address;
        }

        var forwardedFor = context.Request.Headers[ForwardedForHeader];
        var valueIndex = forwardedFor.Count - 1;
        var end = -1;
        while (trustedProxies.Contains(address)
            && TryTakeLastEntry(forwardedFor, ref valueIndex, ref end, out var entry))
        {
            // A proxy that forwards "unknown" (or anything else that is not an address) is the furthest hop known.
            if (!IPEndPoint.TryParse(entry, out var endPoint))
            {
                break;
            }

            address = TrustedProxyAllowlist.Normalize(endPoint.Address);
        }

        return address;
    }

    /// <summary>Removes the right-most non-empty entry from the comma-separated <paramref name="values"/>.</summary>
    private static bool TryTakeLastEntry(
        StringValues values,
        ref int valueIndex,
        ref int end,
        out ReadOnlySpan<char> entry)
    {
        while (valueIndex >= 0)
        {
            var value = values[valueIndex].AsSpan();
            var segmentEnd = end < 0 ? value.Length : end;
            var segmentStart = segmentEnd;
            while (segmentStart > 0 && value[segmentStart - 1] != ',')
            {
                segmentStart--;
            }

            entry = value[segmentStart..segmentEnd].Trim();
            if (segmentStart == 0)
            {
                valueIndex--;
                end = -1;
            }
            else
            {
                end = segmentStart - 1;
            }

            if (!entry.IsEmpty)
            {
                return true;
            }
        }

        entry = default;
        return false;
    }
}
