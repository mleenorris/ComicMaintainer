using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class ProcessingHistoryServiceCoverageTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Mock<ILogger<ProcessingHistoryService>> _logger = new();
    private ServiceProvider _provider = null!;
    private ProcessingHistoryService _service = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        var factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        _service = new ProcessingHistoryService(factory, _logger.Object);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task AddHistoryEntryAsync_LogsAndSwallowsADatabaseFailure()
    {
        var entryId = Guid.NewGuid();
        await _service.AddHistoryEntryAsync(new ProcessingHistoryEntry
        {
            Id = entryId,
            FilePath = "/lib/first.cbz",
            Action = "Process",
            Timestamp = DateTime.UtcNow,
            Success = true
        });

        // Re-using the entry id violates its unique index; recording history must never
        // break the processing that triggered it.
        await _service.AddHistoryEntryAsync(new ProcessingHistoryEntry
        {
            Id = entryId,
            FilePath = "/lib/second.cbz",
            Action = "Rename",
            Timestamp = DateTime.UtcNow,
            Success = false
        });

        var (history, total) = await _service.GetHistoryAsync();
        Assert.Equal(1, total);
        var stored = Assert.Single(history);
        Assert.Equal("/lib/first.cbz", stored.FilePath);
        _logger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v!.ToString()!.Contains("Error adding processing history entry")),
                It.Is<Exception>(e => e is DbUpdateException),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
