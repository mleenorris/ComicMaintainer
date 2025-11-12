namespace ComicMaintainer.Core.Configuration;

/// <summary>
/// Configuration settings for Authelia integration
/// </summary>
public class AutheliaSettings
{
    /// <summary>
    /// Enable Authelia forward authentication mode
    /// When enabled, the application will trust authentication headers from Authelia
    /// </summary>
    public bool Enabled { get; set; } = false;
    
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
