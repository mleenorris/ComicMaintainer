using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Keeps outbound requests for user- or provider-supplied URLs (series cover images) on the public internet, so they
/// cannot be pointed at the Docker network, the LAN, the host itself or a cloud metadata endpoint (SSRF).
/// </summary>
/// <remarks>
/// Checking a URL's host before the request is not enough on its own: a public server can redirect to an internal
/// address, and a hostname with a short DNS TTL can resolve to a public address for the check and a private one for
/// the connection (DNS rebinding). The handler from <see cref="CreateHandler"/> therefore follows no redirects (the
/// caller follows them itself, checking every hop) and resolves the host itself when it connects, refusing to connect
/// when any address is not public and connecting only to the addresses it checked.
/// </remarks>
public static class PublicNetworkGuard
{
    /// <summary>The start of the message of the exception thrown for a host that is not on the public internet.</summary>
    public const string NonPublicAddressMessage = "resolves to a non-public address";

    /// <summary>Resolves a host name to its addresses.</summary>
    public delegate Task<IPAddress[]> HostResolver(string host, CancellationToken cancellationToken);

    private static readonly HostResolver SystemResolver = (host, cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);

    /// <summary>
    /// A handler for requests that must only reach the public internet. It follows no redirects, uses no proxy (a
    /// proxy would make the connection, so the address could not be checked) and, when it connects, refuses any host
    /// that resolves to an address <see cref="IsBlockedAddress"/> rejects.
    /// </summary>
    /// <param name="resolver">Resolves host names; the system resolver when <see langword="null"/>. For tests.</param>
    public static SocketsHttpHandler CreateHandler(HostResolver? resolver = null)
    {
        var resolve = resolver ?? SystemResolver;
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            // Re-resolve now and then, so a long-lived pooled connection does not outlive a DNS change.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = (context, cancellationToken) => ConnectAsync(context.DnsEndPoint, resolve, cancellationToken),
        };
    }

    /// <summary>
    /// The addresses <paramref name="host"/> resolves to (the address itself for an IP literal), all of them public.
    /// </summary>
    /// <exception cref="NonPublicAddressException">The host resolves to an address that is not public.</exception>
    /// <exception cref="HttpRequestException">The host cannot be resolved or resolves to nothing.</exception>
    public static async Task<IPAddress[]> ResolvePublicAddressesAsync(string host, HostResolver? resolver, CancellationToken cancellationToken)
    {
        var name = host.Trim('[', ']');
        IPAddress[] addresses;
        if (IPAddress.TryParse(name, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await (resolver ?? SystemResolver)(name, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                throw new HttpRequestException($"Cannot resolve host '{LoggingHelper.SanitizeForLog(name)}': {ex.Message}", ex);
            }
        }

        if (addresses.Length == 0)
        {
            throw new HttpRequestException($"Host '{LoggingHelper.SanitizeForLog(name)}' did not resolve to any address");
        }

        if (Array.Exists(addresses, IsBlockedAddress))
        {
            throw new NonPublicAddressException($"Host '{LoggingHelper.SanitizeForLog(name)}' {NonPublicAddressMessage}");
        }

        return addresses;
    }

    private static async ValueTask<Stream> ConnectAsync(DnsEndPoint endPoint, HostResolver resolver, CancellationToken cancellationToken)
    {
        var addresses = await ResolvePublicAddressesAsync(endPoint.Host, resolver, cancellationToken);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            // Connect to the checked addresses themselves, never to the name: resolving it again could give another answer.
            await socket.ConnectAsync(addresses, endPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether <paramref name="address"/> is outside the public internet: unspecified, loopback, private (RFC 1918,
    /// unique local), shared (CGNAT), link-local (including the 169.254.169.254 cloud metadata endpoint), site-local,
    /// multicast, broadcast, reserved, benchmarking or documentation ranges, or an IPv6 address that embeds a blocked
    /// IPv4 address (IPv4-mapped, IPv4-compatible, NAT64, 6to4) or cannot be checked (Teredo, local-use NAT64).
    /// </summary>
    public static bool IsBlockedAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsBlockedIPv4(address.GetAddressBytes());
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return true;
        }

        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();

        // ::ffff:0:0/96, IPv4-mapped: the embedded IPv4 address is what is reached.
        if (address.IsIPv4MappedToIPv6)
        {
            return IsBlockedIPv4(bytes.AsSpan(12, 4));
        }

        // ::/96: the unspecified address ::, the loopback ::1 and the deprecated IPv4-compatible ::a.b.c.d.
        if (bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0)
        {
            return true;
        }

        // 64:ff9b::/96, well-known NAT64 prefix: the gateway connects to the embedded IPv4 address.
        if (StartsWith(bytes, [0x00, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0]))
        {
            return IsBlockedIPv4(bytes.AsSpan(12, 4));
        }

        // 64:ff9b:1::/48, local-use NAT64: its translation is site-specific.
        if (StartsWith(bytes, [0x00, 0x64, 0xff, 0x9b, 0x00, 0x01]))
        {
            return true;
        }

        // 100::/64, discard-only.
        if (StartsWith(bytes, [0x01, 0x00, 0, 0, 0, 0, 0, 0]))
        {
            return true;
        }

        // 2001::/32 Teredo (the IPv4 address is obfuscated) and 2001:db8::/32 documentation.
        if (StartsWith(bytes, [0x20, 0x01, 0x00, 0x00]) || StartsWith(bytes, [0x20, 0x01, 0x0d, 0xb8]))
        {
            return true;
        }

        // 2002::/16, 6to4: the relay connects to the embedded IPv4 address.
        if (StartsWith(bytes, [0x20, 0x02]))
        {
            return IsBlockedIPv4(bytes.AsSpan(2, 4));
        }

        // fc00::/7, unique local addresses.
        return (bytes[0] & 0xFE) == 0xFC;
    }

    private static bool StartsWith(byte[] bytes, ReadOnlySpan<byte> prefix) => bytes.AsSpan().StartsWith(prefix);

    // IPv4 blocks that are not on the public internet, as (network address, prefix length).
    private static readonly (uint Network, int PrefixLength)[] BlockedIPv4Blocks =
    [
        (IPv4(0, 0, 0), 8),       // 0.0.0.0/8 "this network"
        (IPv4(10, 0, 0), 8),      // 10.0.0.0/8 private
        (IPv4(100, 64, 0), 10),   // 100.64.0.0/10 shared address space (CGNAT)
        (IPv4(127, 0, 0), 8),     // 127.0.0.0/8 loopback
        (IPv4(169, 254, 0), 16),  // 169.254.0.0/16 link-local, cloud metadata
        (IPv4(172, 16, 0), 12),   // 172.16.0.0/12 private
        (IPv4(192, 0, 0), 24),    // 192.0.0.0/24 IETF protocol assignments
        (IPv4(192, 0, 2), 24),    // 192.0.2.0/24 TEST-NET-1
        (IPv4(192, 88, 99), 24),  // 192.88.99.0/24 6to4 relay anycast
        (IPv4(192, 168, 0), 16),  // 192.168.0.0/16 private
        (IPv4(198, 18, 0), 15),   // 198.18.0.0/15 benchmarking
        (IPv4(198, 51, 100), 24), // 198.51.100.0/24 TEST-NET-2
        (IPv4(203, 0, 113), 24),  // 203.0.113.0/24 TEST-NET-3
        (IPv4(224, 0, 0), 3),     // 224.0.0.0/4 multicast and 240.0.0.0/4 reserved, broadcast included
    ];

    private static uint IPv4(byte first, byte second, byte third) =>
        ((uint)first << 24) | ((uint)second << 16) | ((uint)third << 8);

    private static bool IsBlockedIPv4(ReadOnlySpan<byte> bytes)
    {
        var address = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        return BlockedIPv4Blocks.Any(block => (address & (uint.MaxValue << (32 - block.PrefixLength))) == block.Network);
    }
}

/// <summary>
/// Thrown by <see cref="PublicNetworkGuard"/> for a host that resolves to an address outside the public internet.
/// </summary>
public sealed class NonPublicAddressException(string message) : HttpRequestException(message);
