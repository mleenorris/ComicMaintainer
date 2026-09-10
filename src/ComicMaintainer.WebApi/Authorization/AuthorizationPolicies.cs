namespace ComicMaintainer.WebApi.Authorization;

/// <summary>
/// Names of the application's authorization policies.
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>
    /// Required for any operation that mutates the comic library or triggers
    /// processing (rename, normalize, delete, metadata writes, batch jobs).
    /// Satisfied by any authenticated user who is <em>not</em> in the
    /// <c>ReadOnly</c> role.
    /// </summary>
    public const string CanModifyLibrary = "CanModifyLibrary";

    /// <summary>
    /// Required for administrative operations (changing application settings,
    /// creating users, restarting the service). Satisfied by members of the
    /// <c>Admin</c> role.
    /// </summary>
    public const string CanAdminister = "CanAdminister";
}
