using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class DatabaseCleanupHostedServiceTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Mock<IFileStoreService> _mockFileStore;
    private readonly Mock<IOptionsMonitor<AppSettings>> _mockOptions;
    private readonly Mock<ILogger<DatabaseCleanupHostedService>> _mockLogger;
    private readonly DatabaseCleanupHostedService _service;
    private readonly CancellationTokenSource _cts;

    public DatabaseCleanupHostedServiceTests()
    {
        _mockFileStore = new Mock<IFileStoreService>();
        _mockOptions = new Mock<IOptionsMonitor<AppSettings>>();
        _mockLogger = new Mock<ILogger<DatabaseCleanupHostedService>>();
        _cts = new CancellationTokenSource();

        // Setup default app settings
        var appSettings = new AppSettings
        {
            DatabaseCleanupIntervalHours = 12
        };
        _mockOptions.Setup(x => x.CurrentValue).Returns(appSettings);
        SetupCleanup(Completed(0));

        _service = CreateService(_mockOptions.Object);
    }

    private DatabaseCleanupHostedService CreateService(IOptionsMonitor<AppSettings> options, TimeSpan? startupDelay = null) =>
        new(_mockFileStore.Object, options, _mockLogger.Object, startupDelay: startupDelay ?? TimeSpan.Zero);

    private static StaleCleanupResult Completed(int removed) =>
        new() { Status = StaleCleanupStatus.Completed, RemovedCount = removed };

    private void SetupCleanup(StaleCleanupResult result) =>
        _mockFileStore
            .Setup(x => x.CleanupStaleEntriesAsync(It.IsAny<StaleCleanupOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    /// <summary>Starts the service and waits for its start-up cleanup run.</summary>
    private static async Task StartAndWaitForStartupRunAsync(DatabaseCleanupHostedService service, CancellationToken token)
    {
        await service.StartAsync(token);
        await service.StartupRun.WaitAsync(Timeout);
    }

    private void VerifyLog(LogLevel level, string fragment, Times times) =>
        _mockLogger.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v!.ToString()!.Contains(fragment)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);

    [Fact]
    public async Task StartAsync_RunsAScheduledCleanupShortlyAfterStartup()
    {
        // Act
        await StartAndWaitForStartupRunAsync(_service, _cts.Token);

        // Assert: the start-up run is a scheduled (guarded) run, never a manual one.
        _mockFileStore.Verify(
            x => x.CleanupStaleEntriesAsync(StaleCleanupOptions.Scheduled, It.IsAny<CancellationToken>()),
            Times.Once);
        VerifyLog(LogLevel.Information, "Starting Database Cleanup", Times.Once());
    }

    [Fact]
    public async Task StartAsync_ReturnsBeforeTheStartupCleanupRuns()
    {
        // A share that is still being mounted must not be swept while the host starts, and
        // the start of the application must not wait for a sweep of the whole library.
        using var service = CreateService(_mockOptions.Object, startupDelay: TimeSpan.FromHours(1));

        await service.StartAsync(_cts.Token).WaitAsync(Timeout);

        Assert.False(service.StartupRun.IsCompleted);
        _mockFileStore.Verify(
            x => x.CleanupStaleEntriesAsync(It.IsAny<StaleCleanupOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Stopping during the delay ends the pending run without running it.
        await service.StopAsync(CancellationToken.None).WaitAsync(Timeout);
        Assert.True(service.StartupRun.IsCompleted);
        _mockFileStore.Verify(
            x => x.CleanupStaleEntriesAsync(It.IsAny<StaleCleanupOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StartAsync_WithIntervalGreaterThanZero_SchedulesPeriodicCleanup()
    {
        // Arrange
        _mockOptions.Setup(x => x.CurrentValue).Returns(new AppSettings { DatabaseCleanupIntervalHours = 6 });

        // Act
        await StartAndWaitForStartupRunAsync(_service, _cts.Token);

        // Assert - verify cleanup interval was logged
        VerifyLog(LogLevel.Information, "every 6 hours", Times.Once());
    }

    [Fact]
    public async Task StartAsync_WithIntervalZero_OnlyRunsOnStartup()
    {
        // Arrange
        _mockOptions.Setup(x => x.CurrentValue).Returns(new AppSettings { DatabaseCleanupIntervalHours = 0 });

        // Act
        await StartAndWaitForStartupRunAsync(_service, _cts.Token);

        // Assert - verify "run only on startup" was logged
        VerifyLog(LogLevel.Information, "run only on startup", Times.Once());
    }

    [Fact]
    public async Task StartAsync_CleanupRemovesEntries_LogsCount()
    {
        // Arrange
        SetupCleanup(Completed(10));

        // Act
        await StartAndWaitForStartupRunAsync(_service, _cts.Token);

        // Assert
        VerifyLog(LogLevel.Information, "removed 10", Times.Once());
    }

    [Theory]
    [InlineData(StaleCleanupStatus.WatchedDirectoryUnavailable)]
    [InlineData(StaleCleanupStatus.WatchedDirectoryEmpty)]
    [InlineData(StaleCleanupStatus.SentinelMissing)]
    [InlineData(StaleCleanupStatus.ThresholdExceeded)]
    [InlineData(StaleCleanupStatus.Failed)]
    public async Task StartAsync_GuardedCleanup_LogsAWarningInsteadOfACount(StaleCleanupStatus status)
    {
        // Arrange
        SetupCleanup(new StaleCleanupResult { Status = status, Message = "the library looks unmounted" });

        // Act
        await StartAndWaitForStartupRunAsync(_service, _cts.Token);

        // Assert
        VerifyLog(LogLevel.Warning, $"Scheduled database cleanup removed nothing ({status}): the library looks unmounted", Times.Once());
        VerifyLog(LogLevel.Information, "Database cleanup completed", Times.Never());
    }

    [Fact]
    public async Task StartAsync_CancelledCleanupResult_LogsCancellation()
    {
        // Arrange
        SetupCleanup(new StaleCleanupResult { Status = StaleCleanupStatus.Cancelled });

        // Act
        await StartAndWaitForStartupRunAsync(_service, _cts.Token);

        // Assert
        VerifyLog(LogLevel.Information, "Database cleanup was cancelled", Times.Once());
        VerifyLog(LogLevel.Warning, "removed nothing", Times.Never());
    }

    [Fact]
    public async Task StartAsync_WithCancelledToken_LogsCancellation()
    {
        // Arrange
        _mockFileStore
            .Setup(x => x.CleanupStaleEntriesAsync(It.IsAny<StaleCleanupOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // Act
        await StartAndWaitForStartupRunAsync(_service, _cts.Token);

        // Assert - should log the cancellation
        VerifyLog(LogLevel.Information, "cancelled", Times.Once());
    }

    [Fact]
    public async Task StartAsync_WithException_LogsError()
    {
        // Arrange
        _mockFileStore
            .Setup(x => x.CleanupStaleEntriesAsync(It.IsAny<StaleCleanupOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test error"));

        // Act
        await StartAndWaitForStartupRunAsync(_service, _cts.Token);

        // Assert - should log error but not throw
        VerifyLog(LogLevel.Error, "Error", Times.Once());
    }

    [Fact]
    public async Task ScheduledRun_PrunesProcessingHistoryToTheRetentionSettings()
    {
        _mockOptions.Setup(x => x.CurrentValue).Returns(new AppSettings
        {
            DatabaseCleanupIntervalHours = 12,
            ProcessingHistoryRetentionDays = 30,
            ProcessingHistoryMaxEntries = 1000,
        });
        var history = new Mock<IProcessingHistoryService>();
        history.Setup(h => h.PruneAsync(30, 1000, It.IsAny<CancellationToken>())).ReturnsAsync(250);
        using var service = new DatabaseCleanupHostedService(
            _mockFileStore.Object, _mockOptions.Object, _mockLogger.Object,
            startupDelay: TimeSpan.Zero, historyService: history.Object);

        await StartAndWaitForStartupRunAsync(service, _cts.Token);

        history.Verify(h => h.PruneAsync(30, 1000, It.IsAny<CancellationToken>()), Times.Once);
        VerifyLog(LogLevel.Information, "Pruned 250 processing history entries", Times.Once());
    }

    [Fact]
    public async Task ScheduledRun_PrunesHistoryEvenWhenTheStaleCleanupWasGuarded()
    {
        SetupCleanup(new StaleCleanupResult { Status = StaleCleanupStatus.WatchedDirectoryEmpty, Message = "unmounted" });
        var history = new Mock<IProcessingHistoryService>();
        using var service = new DatabaseCleanupHostedService(
            _mockFileStore.Object, _mockOptions.Object, _mockLogger.Object,
            startupDelay: TimeSpan.Zero, historyService: history.Object);

        await StartAndWaitForStartupRunAsync(service, _cts.Token);

        history.Verify(h => h.PruneAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScheduledRun_HistoryPruneFailure_IsLoggedAndDoesNotFailTheRun()
    {
        var history = new Mock<IProcessingHistoryService>();
        history.Setup(h => h.PruneAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        using var service = new DatabaseCleanupHostedService(
            _mockFileStore.Object, _mockOptions.Object, _mockLogger.Object,
            startupDelay: TimeSpan.Zero, historyService: history.Object);

        await StartAndWaitForStartupRunAsync(service, _cts.Token);

        VerifyLog(LogLevel.Warning, "Failed to prune processing history", Times.Once());
        VerifyLog(LogLevel.Error, "Error during scheduled database cleanup", Times.Never());
    }

    [Fact]
    public async Task ScheduledRun_RefreshesTheSqlitePlannerStatistics()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.ComicFiles.AddRange(Enumerable.Range(1, 20).Select(i => new ComicFileEntity
            {
                FilePath = $"/library/Saga/Saga {i:000}.cbz",
                FileName = $"Saga {i:000}.cbz",
                Directory = "/library/Saga",
                FileSize = i,
                LastModified = DateTime.UtcNow.AddMinutes(-i),
            }));
            await db.SaveChangesAsync();
        }
        using var service = new DatabaseCleanupHostedService(
            _mockFileStore.Object, _mockOptions.Object, _mockLogger.Object,
            dbContextFactory: factory, startupDelay: TimeSpan.Zero);

        await StartAndWaitForStartupRunAsync(service, _cts.Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_stat1 WHERE tbl = 'ComicFiles'";
        Assert.True(Convert.ToInt64(await command.ExecuteScalarAsync()) > 0);
        VerifyLog(LogLevel.Warning, "Failed to refresh SQLite query-planner statistics", Times.Never());
    }

    [Fact]
    public async Task StopAsync_LogsStoppingMessage()
    {
        // Arrange
        await StartAndWaitForStartupRunAsync(_service, _cts.Token);

        // Act
        await _service.StopAsync(_cts.Token);

        // Assert
        VerifyLog(LogLevel.Information, "Stopping Database Cleanup", Times.Once());
    }

    [Fact]
    public async Task StopAsync_WithoutStart_DoesNotThrow()
    {
        // Act & Assert - should not throw
        await _service.StopAsync(_cts.Token);
    }

    [Fact]
    public void Dispose_DisposesResourcesSafely()
    {
        // Act & Assert - should not throw
        _service.Dispose();
    }

    [Fact]
    public async Task Dispose_AfterStart_DisposesResourcesSafely()
    {
        // Arrange
        await StartAndWaitForStartupRunAsync(_service, _cts.Token);

        // Act & Assert - should not throw
        _service.Dispose();
    }

    [Fact]
    public async Task StartAsync_WithNegativeInterval_OnlyRunsOnStartup()
    {
        // Arrange
        _mockOptions.Setup(x => x.CurrentValue).Returns(new AppSettings { DatabaseCleanupIntervalHours = -1 });
        using var service = CreateService(_mockOptions.Object);

        // Act
        await StartAndWaitForStartupRunAsync(service, _cts.Token);

        // Assert - should log "run only on startup"
        VerifyLog(LogLevel.Information, "run only on startup", Times.Once());
    }

    [Fact]
    public async Task StopAsync_MultipleTimes_DoesNotThrow()
    {
        // Arrange
        await StartAndWaitForStartupRunAsync(_service, _cts.Token);
        await _service.StopAsync(_cts.Token);

        // Act & Assert - calling StopAsync multiple times should not throw
        await _service.StopAsync(_cts.Token);
        await _service.StopAsync(_cts.Token);
    }

    [Fact]
    public async Task StartAsync_LogsScheduledCleanupMessage()
    {
        // Act
        await StartAndWaitForStartupRunAsync(_service, _cts.Token);

        // Assert - should log starting message
        VerifyLog(LogLevel.Information, "Starting scheduled database cleanup", Times.Once());
    }

    [Fact]
    public async Task SettingsChange_ToNonZeroInterval_ReschedulesTimerWithoutRestart()
    {
        // Arrange — start with cleanup-on-startup-only (interval = 0), then change to 6 hours.
        var monitor = new TestOptionsMonitor<AppSettings>(new AppSettings
        {
            DatabaseCleanupIntervalHours = 0
        });

        using var service = CreateService(monitor);

        await StartAndWaitForStartupRunAsync(service, _cts.Token);

        // Reset call history so we can verify the post-change log
        _mockLogger.Invocations.Clear();

        // Act — change the cleanup interval at runtime
        monitor.Set(new AppSettings
        {
            DatabaseCleanupIntervalHours = 6
        });

        // Assert — timer reconfiguration should be logged without restarting the service
        VerifyLog(LogLevel.Information, "every 6 hours", Times.Once());
    }

    [Fact]
    public async Task SettingsChange_ToZeroInterval_StopsPeriodicTimerWithoutRestart()
    {
        // Arrange — start with a 6-hour interval, then change to 0 (only-on-startup)
        var monitor = new TestOptionsMonitor<AppSettings>(new AppSettings
        {
            DatabaseCleanupIntervalHours = 6
        });

        using var service = CreateService(monitor);

        await StartAndWaitForStartupRunAsync(service, _cts.Token);

        _mockLogger.Invocations.Clear();

        // Act — change interval to 0 at runtime
        monitor.Set(new AppSettings
        {
            DatabaseCleanupIntervalHours = 0
        });

        // Assert — should log "run only on startup" without restarting
        VerifyLog(LogLevel.Information, "run only on startup", Times.Once());
    }

    public void Dispose()
    {
        _cts.Dispose();
        _service.Dispose();
    }
}
