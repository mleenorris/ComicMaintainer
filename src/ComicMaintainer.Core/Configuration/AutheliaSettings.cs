namespace ComicMaintainer.Core.Configuration;

/// <summary>
/// Configuration settings for Authelia integration
/// </summary>
public class AutheliaSettings
{
    /// <summary>
    /// Enable Authelia forward authentication mode
    /// When enabled, the application will trust authentication headers from the proxies in <see cref="TrustedProxies"/>
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Comma-separated IP addresses and/or CIDR ranges (IPv4 or IPv6) of the reverse proxies allowed to send
    /// the Authelia headers, e.g. "172.18.0.0/16, 10.0.0.5, fd00::/8" (environment variable AUTHELIA_TRUSTED_PROXIES).
    /// Required when <see cref="Enabled"/> is true: the application refuses to start without a valid list, and the
    /// headers are ignored on requests from any other address. Hostnames are not supported.
    /// </summary>
    public string? TrustedProxies { get; set; }

    /// <summary>
    /// Address at which ComicMaintainer can reach Authelia directly, including Authelia's server path if it has one,
    /// e.g. "http://authelia:9091" (environment variable AUTHELIA_URL). When set, Authelia's health endpoint is checked
    /// in the background and its outages are logged: while Authelia is down the reverse proxy rejects every request
    /// before it reaches ComicMaintainer, so nothing else would be logged. Independent of <see cref="Enabled"/>.
    /// </summary>
    public string? Url { get; set; }
    
    /// <summary>
    /// Header name for the authenticated username (default: Remote-User)
    /// </summary>
    public string UserHeader { get; set; } = "Remote-User";
    
    /// <summary>
    /// Header name for the user's email (default: Remote-Email)
    /// </summary>
    public string EmailHeader { get; set; } = "Remote-Email";
    
    /// <summary>
    /// Header name for the user's display name (default: Remote-Name)
    /// </summary>
    public string NameHeader { get; set; } = "Remote-Name";
    
    /// <summary>
    /// Header name for the user's groups (default: Remote-Groups)
    /// </summary>
    public string GroupsHeader { get; set; } = "Remote-Groups";
    
    /// <summary>
    /// Default role to assign to users authenticated via Authelia
    /// Options: Admin, User, ReadOnly (default: User)
    /// </summary>
    public string DefaultRole { get; set; } = "User";
    
    /// <summary>
    /// Comma-separated list of Authelia groups that should be assigned the Admin role
    /// Example: "admins,administrators"
    /// </summary>
    public string? AdminGroups { get; set; }
}
