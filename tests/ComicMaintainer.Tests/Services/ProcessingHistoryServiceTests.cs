using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class ProcessingHistoryServiceTests
{
    private readonly ProcessingHistoryService _service;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _dbName;
    private readonly Mock<ILogger<ProcessingHistoryService>> _mockLogger;

    public ProcessingHistoryServiceTests()
    {
        _dbName = $"TestDb_{Guid.NewGuid()}";

        // Setup in-memory database
        var services = new ServiceCollection();
        services.AddDbContext<ComicMaintainerDbContext>(opt =>
            opt.UseInMemoryDatabase(_dbName));
        _serviceProvider = services.BuildServiceProvider();
        
        _mockLogger = new Mock<ILogger<ProcessingHistoryService>>();
        _service = new ProcessingHistoryService(_serviceProvider, _mockLogger.Object);
    }

    [Fact]
    public async Task AddHistoryEntryAsync_ValidEntry_AddsToDatabase()
    {
        // Arrange
        var entry = new ProcessingHistoryEntry
        {
            Id = Guid.NewGuid(),
            FilePath = "/test/file.cbz",
            Action = "Process",
            Timestamp = DateTime.UtcNow,
            Success = true,
            ErrorMessage = null
        };

        // Act
        await _service.AddHistoryEntryAsync(entry);

        // Assert
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
        var historyEntries = await dbContext.ProcessingHistory.ToListAsync();
        
        Assert.Single(historyEntries);
        var savedEntry = historyEntries.First();
        Assert.Equal(entry.FilePath, savedEntry.FilePath);
        Assert.Equal(entry.Action, savedEntry.Action);
        Assert.Equal(entry.Success, savedEntry.Success);
    }

    [Fact]
    public async Task AddHistoryEntryAsync_WithCancelledToken_LogsWarningAndDoesNotThrow()
    {
        // Arrange
        var entry = new ProcessingHistoryEntry
        {
            Id = Guid.NewGuid(),
            FilePath = "/test/file.cbz",
            Action = "Process",
            Timestamp = DateTime.UtcNow,
            Success = true
        };
        
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act & Assert - Should not throw
        await _service.AddHistoryEntryAsync(entry, cts.Token);
        
        // Verify warning was logged
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("cancelled")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsEntriesOrderedByTimestamp()
    {
        // Arrange
        var entry1 = new ProcessingHistoryEntry
        {
            Id = Guid.NewGuid(),
            FilePath = "/test/file1.cbz",
            Action = "Process",
            Timestamp = DateTime.UtcNow.AddMinutes(-10),
            Success = true
        };
        
        var entry2 = new ProcessingHistoryEntry
        {
            Id = Guid.NewGuid(),
            FilePath = "/test/file2.cbz",
            Action = "Rename",
            Timestamp = DateTime.UtcNow.AddMinutes(-5),
            Success = true
        };
        
        var entry3 = new ProcessingHistoryEntry
        {
            Id = Guid.NewGuid(),
            FilePath = "/test/file3.cbz",
            Action = "Normalize",
            Timestamp = DateTime.UtcNow,
            Success = false,
            ErrorMessage = "Test error"
        };

        await _service.AddHistoryEntryAsync(entry1);
        await _service.AddHistoryEntryAsync(entry2);
        await _service.AddHistoryEntryAsync(entry3);

        // Act
        var (history, total) = await _service.GetHistoryAsync(limit: 10);
        var historyList = history.ToList();

        // Assert
        Assert.Equal(3, total);
        Assert.Equal(3, historyList.Count);
        
        // Should be ordered by timestamp descending (most recent first)
        Assert.Equal(entry3.FilePath, historyList[0].FilePath);
        Assert.Equal(entry2.FilePath, historyList[1].FilePath);
        Assert.Equal(entry1.FilePath, historyList[2].FilePath);
    }

    [Fact]
    public async Task GetHistoryAsync_WithPagination_ReturnsCorrectSubset()
    {
        // Arrange - Add 5 entries
        for (int i = 0; i < 5; i++)
        {
            var entry = new ProcessingHistoryEntry
            {
                Id = Guid.NewGuid(),
                FilePath = $"/test/file{i}.cbz",
                Action = "Process",
                Timestamp = DateTime.UtcNow.AddMinutes(-i),
                Success = true
            };
            await _service.AddHistoryEntryAsync(entry);
        }

        // Act - Get page 2 with 2 items per page
        var (history, total) = await _service.GetHistoryAsync(limit: 2, offset: 2);
        var historyList = history.ToList();

        // Assert
        Assert.Equal(5, total);
        Assert.Equal(2, historyList.Count);
    }

    [Fact]
    public async Task AddHistoryEntryAsync_WithBeforeAfterMetadata_SavesAllFields()
    {
        // Arrange
        var entry = new ProcessingHistoryEntry
        {
            Id = Guid.NewGuid(),
            FilePath = "/test/file.cbz",
            Action = "Rename",
            Timestamp = DateTime.UtcNow,
            Success = true,
            BeforeFilename = "old_name.cbz",
            AfterFilename = "new_name.cbz",
            BeforeTitle = "Old Title",
            AfterTitle = "New Title",
            BeforeSeries = "Old Series",
            AfterSeries = "New Series",
            BeforeIssue = "1",
            AfterIssue = "001",
            BeforePublisher = "Old Publisher",
            AfterPublisher = "New Publisher",
            BeforeYear = 2020,
            AfterYear = 2021,
            BeforeVolume = "1",
            AfterVolume = "01"
        };

        // Act
        await _service.AddHistoryEntryAsync(entry);

        // Assert
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
        var savedEntry = await dbContext.ProcessingHistory.FirstOrDefaultAsync();
        
        Assert.NotNull(savedEntry);
        Assert.Equal(entry.BeforeFilename, savedEntry.BeforeFilename);
        Assert.Equal(entry.AfterFilename, savedEntry.AfterFilename);
        Assert.Equal(entry.BeforeTitle, savedEntry.BeforeTitle);
        Assert.Equal(entry.AfterTitle, savedEntry.AfterTitle);
        Assert.Equal(entry.BeforeSeries, savedEntry.BeforeSeries);
        Assert.Equal(entry.AfterSeries, savedEntry.AfterSeries);
        Assert.Equal(entry.BeforeIssue, savedEntry.BeforeIssue);
        Assert.Equal(entry.AfterIssue, savedEntry.AfterIssue);
        Assert.Equal(entry.BeforePublisher, savedEntry.BeforePublisher);
        Assert.Equal(entry.AfterPublisher, savedEntry.AfterPublisher);
        Assert.Equal(entry.BeforeYear, savedEntry.BeforeYear);
        Assert.Equal(entry.AfterYear, savedEntry.AfterYear);
        Assert.Equal(entry.BeforeVolume, savedEntry.BeforeVolume);
        Assert.Equal(entry.AfterVolume, savedEntry.AfterVolume);
    }
}
