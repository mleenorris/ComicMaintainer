using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Reader.Models;
using ComicMaintainer.Core.Reader.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Reader;

/// <summary>
/// Unit tests for <see cref="ReadingProgressService"/> using an in-memory database.
/// </summary>
public class ReadingProgressServiceTests
{
    private readonly ReadingProgressService _service;
    private readonly IServiceProvider _serviceProvider;

    public ReadingProgressServiceTests()
    {
        var dbName = $"TestDb_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        _serviceProvider = services.BuildServiceProvider();

        var factory = _serviceProvider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        _service = new ReadingProgressService(factory, NullLogger<ReadingProgressService>.Instance);
    }

    [Fact]
    public async Task GetProgressAsync_NoProgress_ReturnsNull()
    {
        var result = await _service.GetProgressAsync("user1", "content1");
        Assert.Null(result);
    }

    [Fact]
    public async Task SaveProgressAsync_NewProgress_CanBeRetrieved()
    {
        var progress = new ReadingProgress
        {
            UserId = "user1",
            ContentId = "content1",
            CurrentPage = 5,
            TotalPages = 10,
            PercentComplete = 50.0,
            LastReadAt = DateTime.UtcNow,
            LastReaderMode = ReaderMode.SinglePage,
            ReadingDirection = ReadingDirection.LeftToRight
        };

        await _service.SaveProgressAsync(progress);

        var retrieved = await _service.GetProgressAsync("user1", "content1");
        Assert.NotNull(retrieved);
        Assert.Equal(5, retrieved.CurrentPage);
        Assert.Equal(10, retrieved.TotalPages);
        Assert.Equal(50.0, retrieved.PercentComplete);
        Assert.Equal(ReaderMode.SinglePage, retrieved.LastReaderMode);
        Assert.Equal(ReadingDirection.LeftToRight, retrieved.ReadingDirection);
    }

    [Fact]
    public async Task SaveProgressAsync_ExistingProgress_UpdatesRecord()
    {
        var progress = new ReadingProgress
        {
            UserId = "user1",
            ContentId = "content1",
            CurrentPage = 3,
            TotalPages = 10,
            PercentComplete = 30.0,
            LastReadAt = DateTime.UtcNow
        };
        await _service.SaveProgressAsync(progress);

        progress.CurrentPage = 8;
        progress.PercentComplete = 80.0;
        await _service.SaveProgressAsync(progress);

        var retrieved = await _service.GetProgressAsync("user1", "content1");
        Assert.NotNull(retrieved);
        Assert.Equal(8, retrieved.CurrentPage);
        Assert.Equal(80.0, retrieved.PercentComplete);
    }

    [Fact]
    public async Task SaveProgressAsync_CompletedAt_Persisted()
    {
        var completedAt = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var progress = new ReadingProgress
        {
            UserId = "user1",
            ContentId = "content1",
            CurrentPage = 10,
            TotalPages = 10,
            PercentComplete = 100.0,
            LastReadAt = DateTime.UtcNow,
            CompletedAt = completedAt
        };

        await _service.SaveProgressAsync(progress);

        var retrieved = await _service.GetProgressAsync("user1", "content1");
        Assert.NotNull(retrieved);
        Assert.NotNull(retrieved.CompletedAt);
        Assert.Equal(completedAt, retrieved.CompletedAt.Value);
    }

    [Fact]
    public async Task GetProgressAsync_DifferentUsers_AreSeparate()
    {
        await _service.SaveProgressAsync(new ReadingProgress { UserId = "user1", ContentId = "c1", CurrentPage = 3, TotalPages = 10, LastReadAt = DateTime.UtcNow });
        await _service.SaveProgressAsync(new ReadingProgress { UserId = "user2", ContentId = "c1", CurrentPage = 7, TotalPages = 10, LastReadAt = DateTime.UtcNow });

        var p1 = await _service.GetProgressAsync("user1", "c1");
        var p2 = await _service.GetProgressAsync("user2", "c1");

        Assert.Equal(3, p1!.CurrentPage);
        Assert.Equal(7, p2!.CurrentPage);
    }
}
