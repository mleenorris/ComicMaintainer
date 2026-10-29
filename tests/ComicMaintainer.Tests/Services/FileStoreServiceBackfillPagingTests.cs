using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers paging through the metadata-backfill queue with
/// <see cref="FileStoreService.GetFilesNeedingBackfillAsync"/> against real SQLite, so the
/// file-path cursor is checked as translated SQL.
/// </summary>
public sealed class FileStoreServiceBackfillPagingTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();
        _factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();

        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        db.ComicFiles.AddRange(
            Row("/library/c.cbz", pending: true),
            Row("/library/a.cbz", pending: true),
            Row("/library/e.cbz", pending: true),
            Row("/library/b.cbz", pending: false),
            Row("/library/d.cbz", pending: true, duplicate: true),
            Row("/library/f.cbz", pending: true));
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task GetFilesNeedingBackfillAsync_PagesInPathOrderAfterTheCursor()
    {
        var service = new FileStoreService(
            new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = "/library" }),
            NullLogger<FileStoreService>.Instance,
            _factory);

        var first = await service.GetFilesNeedingBackfillAsync(2);
        var second = await service.GetFilesNeedingBackfillAsync(2, first[^1].FilePath);
        var third = await service.GetFilesNeedingBackfillAsync(2, second[^1].FilePath);

        Assert.Equal(new[] { "/library/a.cbz", "/library/c.cbz" }, first.Select(f => f.FilePath));
        Assert.Equal(new[] { "/library/e.cbz", "/library/f.cbz" }, second.Select(f => f.FilePath));
        Assert.Empty(third);
    }

    private static ComicFileEntity Row(string path, bool pending, bool duplicate = false) => new()
    {
        FilePath = path,
        FileName = Path.GetFileName(path),
        Directory = "/library",
        IsDuplicate = duplicate,
        MetadataVersion = pending ? 2 : 1,
        WrittenMetadataVersion = 1,
    };
}
