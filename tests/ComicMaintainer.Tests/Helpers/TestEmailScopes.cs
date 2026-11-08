using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Tests.Helpers;

/// <summary>
/// <see cref="EmailAccessScope"/>s for tests of e-reader devices, subscriptions
/// and deliveries.
/// </summary>
public static class TestEmailScopes
{
    /// <summary>
    /// An administrator listing every user's rows: sees and changes everything,
    /// which is how every caller behaved before devices had owners. Used by
    /// tests that are not about ownership.
    /// </summary>
    public static EmailAccessScope Everyone { get; } =
        EmailAccessScope.ForUser("test-admin", isAdministrator: true, listEveryone: true);
}
