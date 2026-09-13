using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers the database-backed listing paths for per-user read status.
/// </summary>
/// <remarks>
/// These run against real SQLite rather than the in-memory provider on purpose: the
/// read/unread filter and the <c>IsRead</c> projection are correlated subqueries over
/// <c>UserFileReadStatuses</c>, and the in-memory provider evaluates LINQ that SQLite may
/// be unable to translate. Testing them here proves the query reaches the database instead
/// of silently falling back to client-side evaluation.
/// </remarks>
public class FileStoreServiceReadStatusQueryTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly string _testDirectory =
        Path.Combine(Path.GetTempPath(), $"filestore_readstatus_{Guid.NewGuid()}");

    private ServiceProvider _provider = null!;
    private FileStoreService _service = null!;
    private TestUserContextAccessor _userContext = null!;

    private string ReadFilePath => Path.Combine(_testDirectory, "read.cbz");
    private string UnreadFilePath => Path.Combine(_testDirectory, "unread.cbz");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_testDirectory);
        File.WriteAllText(ReadFilePath, "content");
        File.WriteAllText(UnreadFilePath, "content");

        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        var factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        _userContext = new TestUserContextAccessor("user-a");
        _service = new FileStoreService(
            new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = _testDirectory }),
            new Mock<ILogger<FileStoreService>>().Object,
            factory,
            userContext: _userContext);

        await _service.AddFileAsync(ReadFilePath);
        await _service.AddFileAsync(UnreadFilePath);
        await _service.MarkFileReadAsync(ReadFilePath, true);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
        try
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a leftover temp directory must not fail the test run.
        }
    }

    [Fact]
    public async Task GetFilesPageAsync_ReadFilter_ReturnsOnlyFilesTheCurrentUserHasRead()
    {
        var page = await _service.GetFilesPageAsync(filter: "read");

        var file = Assert.Single(page.Files);
        Assert.Equal(ReadFilePath, file.RelativePath);
        Assert.True(file.Read);
        Assert.Equal(1, page.TotalFiles);
    }

    [Fact]
    public async Task GetFilesPageAsync_UnreadFilter_ReturnsOnlyFilesTheCurrentUserHasNotRead()
    {
        var page = await _service.GetFilesPageAsync(filter: "unread");

        var file = Assert.Single(page.Files);
        Assert.Equal(UnreadFilePath, file.RelativePath);
        Assert.False(file.Read);
    }

    [Fact]
    public async Task GetFilesPageAsync_OtherUser_SeesEverythingAsUnread()
    {
        _userContext.UserId = "user-b";

        var read = await _service.GetFilesPageAsync(filter: "read");
        var unread = await _service.GetFilesPageAsync(filter: "unread");
        var all = await _service.GetFilesPageAsync();

        Assert.Empty(read.Files);
        Assert.Equal(2, unread.Files.Count);
        Assert.All(all.Files, f => Assert.False(f.Read));
    }

    [Fact]
    public async Task GetFilesPageAsync_NoUserInScope_SeesEverythingAsUnread()
    {
        _userContext.UserId = null;

        var read = await _service.GetFilesPageAsync(filter: "read");
        var all = await _service.GetFilesPageAsync();

        Assert.Empty(read.Files);
        Assert.All(all.Files, f => Assert.False(f.Read));
    }

    [Fact]
    public async Task GetFolderFilesAsync_ProjectsReadStatusForTheCurrentUser()
    {
        var forUserA = await _service.GetFolderFilesAsync(string.Empty);

        _userContext.UserId = "user-b";
        var forUserB = await _service.GetFolderFilesAsync(string.Empty);

        Assert.True(forUserA.Single(f => f.RelativePath == ReadFilePath).Read);
        Assert.False(forUserA.Single(f => f.RelativePath == UnreadFilePath).Read);
        Assert.All(forUserB, f => Assert.False(f.Read));
    }
}
