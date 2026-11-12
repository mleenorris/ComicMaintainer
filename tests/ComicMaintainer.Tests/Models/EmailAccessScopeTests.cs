using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;

namespace ComicMaintainer.Tests.Models;

/// <summary>
/// The access rules as they are enforced: through the query filters the
/// delivery services apply to devices and deliveries.
/// </summary>
public class EmailAccessScopeTests
{
    private static readonly string?[] Owners = { "alice", "admin", "bob", null };

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForUser_RequiresAUserId(string userId)
    {
        // An empty id would match the unowned rows only administrators may reach.
        Assert.ThrowsAny<ArgumentException>(() => EmailAccessScope.ForUser(userId));
    }

    [Fact]
    public void AUser_ReachesAndListsOnlyTheirOwnRows()
    {
        var scope = EmailAccessScope.ForUser("alice", listEveryone: true);

        Assert.False(scope.IsAdministrator);
        Assert.False(scope.ListsEveryone);
        AssertCoverage(scope, reachedById: new[] { "alice" }, listed: new[] { "alice" });
    }

    [Fact]
    public void AnAdministrator_ReachesEveryRowAndListsTheirOwnAndUnownedOnes()
    {
        var scope = EmailAccessScope.ForUser("admin", isAdministrator: true);

        AssertCoverage(scope, reachedById: Owners, listed: new[] { "admin", null });
    }

    [Fact]
    public void AnAdministratorListingEveryone_ListsEveryRow()
    {
        var scope = EmailAccessScope.ForUser("admin", isAdministrator: true, listEveryone: true);

        Assert.True(scope.ListsEveryone);
        AssertCoverage(scope, reachedById: Owners, listed: Owners);
    }

    [Fact]
    public void TheSystemScope_ActsForEveryOwnerButOwnsNothing()
    {
        Assert.Null(EmailAccessScope.System.UserId);
        AssertCoverage(EmailAccessScope.System, reachedById: Owners, listed: Owners);
    }

    private static void AssertCoverage(EmailAccessScope scope, string?[] reachedById, string?[] listed)
    {
        var devices = Owners.Select(owner => new EreaderDeviceEntity { OwnerUserId = owner }).AsQueryable();
        var deliveries = Owners.Select(owner => new ComicEmailDeliveryEntity { OwnerUserId = owner }).AsQueryable();

        Assert.Equal(reachedById, devices.AccessibleTo(scope).Select(d => d.OwnerUserId));
        Assert.Equal(reachedById, deliveries.AccessibleTo(scope).Select(d => d.OwnerUserId));
        Assert.Equal(listed, devices.ListedFor(scope).Select(d => d.OwnerUserId));
        Assert.Equal(listed, deliveries.ListedFor(scope).Select(d => d.OwnerUserId));
    }
}
