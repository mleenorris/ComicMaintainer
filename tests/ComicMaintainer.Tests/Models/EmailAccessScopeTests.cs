using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Tests.Models;

public class EmailAccessScopeTests
{
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
        Assert.True(scope.CanAccess("alice"));
        Assert.True(scope.Lists("alice"));
        Assert.False(scope.CanAccess("bob"));
        Assert.False(scope.Lists("bob"));
        Assert.False(scope.CanAccess(null));
        Assert.False(scope.Lists(null));
    }

    [Fact]
    public void AnAdministrator_ReachesEveryRowAndListsTheirOwnAndUnownedOnes()
    {
        var scope = EmailAccessScope.ForUser("admin", isAdministrator: true);

        Assert.True(scope.CanAccess("bob"));
        Assert.True(scope.CanAccess(null));
        Assert.True(scope.Lists("admin"));
        Assert.True(scope.Lists(null));
        Assert.False(scope.Lists("bob"));
    }

    [Fact]
    public void AnAdministratorListingEveryone_ListsEveryRow()
    {
        var scope = EmailAccessScope.ForUser("admin", isAdministrator: true, listEveryone: true);

        Assert.True(scope.ListsEveryone);
        Assert.True(scope.Lists("bob"));
        Assert.True(scope.Lists(null));
    }

    [Fact]
    public void TheSystemScope_ActsForEveryOwnerButOwnsNothing()
    {
        Assert.Null(EmailAccessScope.System.UserId);
        Assert.True(EmailAccessScope.System.CanAccess("bob"));
        Assert.True(EmailAccessScope.System.Lists("bob"));
    }
}
