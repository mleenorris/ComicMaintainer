using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models.Auth;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Tests.Helpers;

/// <summary>
/// A users table for a mocked <c>UserManager.Users</c> that, unlike a list's <c>AsQueryable()</c>, answers EF Core's
/// asynchronous queries.
/// </summary>
internal static class InMemoryUsers
{
    public static IQueryable<ApplicationUser> Of(params ApplicationUser[] users)
    {
        var db = new ComicMaintainerDbContext(new DbContextOptionsBuilder<ComicMaintainerDbContext>()
            .UseInMemoryDatabase("users-" + Guid.NewGuid().ToString("N"))
            .Options);
        db.Users.AddRange(users);
        db.SaveChanges();
        return db.Users;
    }
}
