using ComicMaintainer.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Tests.Data;

/// <summary>
/// Migrations are written by hand (the EF tooling rewrites the whole snapshot), so this checks
/// what the tooling would: the model and the snapshot agree, and the migrations build it.
/// </summary>
public sealed class MigrationModelTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private DbContextOptions<ComicMaintainerDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<ComicMaintainerDbContext>().UseSqlite(_connection).Options;
    }

    public Task DisposeAsync() => _connection.DisposeAsync().AsTask();

    [Fact]
    public async Task Model_MatchesTheSnapshot_AndTheMigrationsBuildIt()
    {
        await using var db = new ComicMaintainerDbContext(_options);

        Assert.False(db.Database.HasPendingModelChanges());
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        // The ComicVine catalogue tables exist and take rows.
        db.ComicVineVolumeLinks.Add(new ComicVineVolumeLinkEntity { SeriesKey = "batman", StartYear = 2016, VolumeId = 91273, LinkedUtc = DateTime.UtcNow });
        db.ComicVineIssues.Add(new ComicVineIssueEntity { Id = 1, VolumeId = 91273, IssueNumber = "1", ListedUtc = DateTime.UtcNow });
        db.ComicVineStoryArcs.Add(new ComicVineStoryArcEntity { Id = 2, Name = "I Am Gotham", FetchedUtc = DateTime.UtcNow });
        db.ComicVineFileStates.Add(new ComicVineFileStateEntity { FilePath = "/c/Batman 001.cbz", IssueId = 1, AppliedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.ComicVineFileStates.CountAsync());
    }
}
