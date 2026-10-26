using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace ComicMaintainer.WebApi.Authentication;

/// <summary>
/// The reverse proxies allowed to send Authelia forward-auth headers, parsed from
/// <c>AUTHELIA_TRUSTED_PROXIES</c> (<see cref="ComicMaintainer.Core.Configuration.AutheliaSettings.TrustedProxies"/>).
/// </summary>
/// <remarks>
/// Entries are IPv4/IPv6 addresses or CIDR ranges. IPv4-mapped IPv6 addresses (<c>::ffff:172.18.0.5</c>)
/// are converted to IPv4, both in the list and for the address being checked, because Kestrel's
/// dual-mode socket reports IPv4 clients in that form.
/// </remarks>
public sealed class TrustedProxyAllowlist
{
    /// <summary>The environment variable that configures the allowlist.</summary>
    public const string EnvironmentVariable = "AUTHELIA_TRUSTED_PROXIES";

    private readonly IPNetwork[] _networks;

    private TrustedProxyAllowlist(IPNetwork[] networks) => _networks = networks;

    /// <summary>The configured networks; a single address is a /32 (IPv4) or /128 (IPv6) network.</summary>
    public IReadOnlyList<IPNetwork> Networks => _networks;

    /// <summary>
    /// Parses a comma-separated list of addresses and CIDR ranges, e.g. <c>172.18.0.0/16, 10.0.0.5, fd00::/8</c>.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> with an actionable <paramref name="error"/> naming the variable (and the offending
    /// entry) when the list is empty or an entry is not a valid address or range.
    /// </returns>
    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out TrustedProxyAllowlist? allowlist,
        [NotNullWhen(false)] out string? error)
    {
        allowlist = null;
        var entries = (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0)
        {
            error = $"{EnvironmentVariable} is required when Authelia is enabled. Set it to the IP address(es) or CIDR " +
                $"range(s) of the reverse proxy that sends the Authelia headers, e.g. {EnvironmentVariable}=172.18.0.0/16 " +
                "(comma-separated, IPv4 or IPv6, no hostnames). See docs/AUTHELIA.md.";
            return false;
        }

        var networks = new IPNetwork[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            if (!TryParseEntry(entries[i], out networks[i]))
            {
                error = $"Invalid {EnvironmentVariable} entry '{entries[i]}'. Use an IPv4 or IPv6 address (e.g. 172.18.0.5) " +
                    "or a CIDR range written with its network address (e.g. 172.18.0.0/16); hostnames are not supported. " +
                    "See docs/AUTHELIA.md.";
                return false;
            }
        }

        allowlist = new TrustedProxyAllowlist(networks);
        error = null;
        return true;
    }

    /// <summary>Whether <paramref name="address"/> belongs to one of the configured networks.</summary>
    public bool Contains(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        var normalized = Normalize(address);
        return Array.Exists(_networks, network => network.Contains(normalized));
    }

    /// <summary>Converts an IPv4-mapped IPv6 address (<c>::ffff:a.b.c.d</c>) to IPv4; other addresses are returned as is.</summary>
    public static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool TryParseEntry(string entry, out IPNetwork network)
    {
        network = default;
        var slash = entry.IndexOf('/');
        var addressText = slash < 0 ? entry : entry[..slash];

        // IPAddress also accepts legacy IPv4 forms ("10" is 0.0.0.10, "010.0.0.0" is octal 8.0.0.0), so only
        // dotted-decimal IPv4 is allowed: an entry must never silently trust a different network.
        if (!IPAddress.TryParse(addressText, out var address)
            || (address.AddressFamily == AddressFamily.InterNetwork && address.ToString() != addressText))
        {
            return false;
        }

        if (slash < 0)
        {
            address = Normalize(address);
            network = new IPNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
            return true;
        }

        // IPNetwork clears the bits after the prefix ("172.18.0.1/16" becomes 172.18.0.0/16): require the network
        // address itself, so that a mistyped entry cannot silently trust a whole range.
        if (!IPNetwork.TryParse(entry, out network) || !network.BaseAddress.Equals(address))
        {
            return false;
        }

        // A valid ::ffff:a.b.c.d/n range has n >= 96; it covers the IPv4 range a.b.c.d/(n - 96).
        if (network.BaseAddress.IsIPv4MappedToIPv6)
        {
            network = new IPNetwork(network.BaseAddress.MapToIPv4(), network.PrefixLength - 96);
        }

        return true;
    }
}
