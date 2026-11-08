namespace ComicMaintainer.Core.Models;

/// <summary>
/// Whose e-reader devices, series subscriptions and email deliveries a request
/// may see and change.
/// </summary>
/// <remarks>
/// Devices belong to the user who saved them; a device's subscriptions and
/// deliveries belong to the device's owner. A user sees and manages only their
/// own. An administrator may act on anyone's by id, and lists everyone's when
/// asked to (<see cref="ListsEveryone"/>); rows without an owner (left over from
/// before devices had owners, when no administrator existed to inherit them)
/// are reachable and listed for administrators only. Callers that are not a
/// user, such as automatic subscription deliveries, use <see cref="System"/>.
/// </remarks>
public sealed class EmailAccessScope
{
    private EmailAccessScope(string? userId, bool isAdministrator, bool listsEveryone)
    {
        UserId = userId;
        IsAdministrator = isAdministrator;
        ListsEveryone = listsEveryone;
    }

    /// <summary>
    /// Background work acting on behalf of every owner: automatic deliveries
    /// for series subscriptions and the delivery worker.
    /// </summary>
    public static EmailAccessScope System { get; } = new(null, isAdministrator: true, listsEveryone: true);

    /// <summary>
    /// The signed-in user the request is made by. Null only for
    /// <see cref="System"/>. New devices are owned by this user.
    /// </summary>
    public string? UserId { get; }

    /// <summary>May see and change every user's devices, subscriptions and deliveries.</summary>
    public bool IsAdministrator { get; }

    /// <summary>
    /// Listings and bulk operations cover every user's rows rather than only
    /// the caller's own. Only ever true for an administrator.
    /// </summary>
    public bool ListsEveryone { get; }

    /// <summary>
    /// The scope of a signed-in user.
    /// </summary>
    /// <param name="userId">The user's id (the NameIdentifier claim).</param>
    /// <param name="isAdministrator">Whether the user may administer the instance.</param>
    /// <param name="listEveryone">
    /// Whether listings should include other users' rows; ignored unless
    /// <paramref name="isAdministrator"/> is true.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="userId"/> is empty.</exception>
    public static EmailAccessScope ForUser(string userId, bool isAdministrator = false, bool listEveryone = false)
    {
        // An empty id would match the rows that have no owner.
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return new EmailAccessScope(userId, isAdministrator, isAdministrator && listEveryone);
    }

    /// <summary>Whether a row owned by <paramref name="ownerUserId"/> may be read or changed by id.</summary>
    public bool CanAccess(string? ownerUserId) =>
        IsAdministrator || (ownerUserId is not null && string.Equals(ownerUserId, UserId, StringComparison.Ordinal));

    /// <summary>
    /// Whether a row owned by <paramref name="ownerUserId"/> appears in listings
    /// and bulk operations: the caller's own rows, unowned legacy rows for an
    /// administrator, and everything when <see cref="ListsEveryone"/>.
    /// </summary>
    public bool Lists(string? ownerUserId) =>
        ListsEveryone
        || (ownerUserId is null
            ? IsAdministrator
            : string.Equals(ownerUserId, UserId, StringComparison.Ordinal));
}
