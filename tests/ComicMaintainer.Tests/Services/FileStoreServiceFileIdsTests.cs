using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// The file row ids OPDS clients use instead of paths, against real SQLite: which paths get an id, and that an id
/// leads back to exactly the file it was given for.
/// </summary>
public sealed class FileStoreServiceFileIdsTests : IAsyncLifetime
{
    private const string Library = "/library";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _factory = null!;
    private FileStoreService _store = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();
        _factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        _store = TestServices.CreateFileStore(
            new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = Library, DuplicateDirectory = string.Empty }),
            NullLogger<FileStoreService>.Instance,
            _factory);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task GetFileIdsAsync_GivesEachTrackedPathItsOwnRowIdAndLeavesOutTheRest()
    {
        var first = await TrackAsync("Saga 001.cbz");
        var second = await TrackAsync("Saga 002.cbz");
        await TrackAsync("Not asked for.cbz");

        var ids = await _store.GetFileIdsAsync([first.Path, second.Path, $"{Library}/Untracked.cbz"]);

        Assert.Equal(2, ids.Count);
        Assert.Equal(first.Id, ids[first.Path]);
        Assert.Equal(second.Id, ids[second.Path]);
    }

    [Fact]
    public async Task GetFileIdsAsync_WithNoPaths_IsEmptyWithoutAQuery()
    {
        var ids = await _store.GetFileIdsAsync([]);

        Assert.Empty(ids);
    }

    // Keys are compared exactly: on the database the two paths are different files.
    [Fact]
    public async Task GetFileIdsAsync_DoesNotMatchAPathInAnotherCase()
    {
        var tracked = await TrackAsync("Saga 001.cbz");

        var ids = await _store.GetFileIdsAsync([tracked.Path.ToUpperInvariant()]);

        Assert.Empty(ids);
    }

    [Fact]
    public async Task GetFilePathAsync_LeadsBackToTheFileOfThatId()
    {
        var first = await TrackAsync("Saga 001.cbz");
        var second = await TrackAsync("Saga 002.cbz");

        Assert.Equal(first.Path, await _store.GetFilePathAsync(first.Id));
        Assert.Equal(second.Path, await _store.GetFilePathAsync(second.Id));
    }

    [Fact]
    public async Task GetFilePathAsync_ForAnIdNoFileHas_IsNull()
    {
        await TrackAsync("Saga 001.cbz");

        Assert.Null(await _store.GetFilePathAsync(int.MaxValue));
    }

    private async Task<(string Path, int Id)> TrackAsync(string name)
    {
        var path = $"{Library}/{name}";
        await using var db = await _factory.CreateDbContextAsync();
        var entity = new ComicFileEntity
        {
            FilePath = path,
            FileName = name,
            Directory = Library,
            FileSize = 100,
            LastModified = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc),
        };
        db.ComicFiles.Add(entity);
        await db.SaveChangesAsync();
        return (path, entity.Id);
    }
}
