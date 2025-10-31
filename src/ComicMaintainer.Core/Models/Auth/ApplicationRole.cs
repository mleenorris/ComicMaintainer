using Microsoft.AspNetCore.Identity;

namespace ComicMaintainer.Core.Models.Auth;

/// <summary>
/// Application role for role-based access control
/// </summary>
public class ApplicationRole : IdentityRole
{
    public string? Description { get; set; }
}
