using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class DatabaseCleanupHostedServiceCoverageTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<ILogger<DatabaseCleanupHostedService>> _logger = new();

    public DatabaseCleanupHostedServiceCoverageTests()
    {
        _fileStore.Setup(f => f.CleanupStaleEntriesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
    }

    private void VerifyLog(LogLevel level, string message, Times times) =>
        _logger.Verify(
            l => l.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString() == message),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);

    [Fact]
    public async Task SettingsChange_AfterStop_DoesNotRearmTheTimer()
    {
        Action<AppSettings, string?>? listener = null;
        var subscription = new Mock<IDisposable>();
        var options = new Mock<IOptionsMonitor<AppSettings>>();
        options.SetupGet(o => o.CurrentValue).Returns(new AppSettings { DatabaseCleanupIntervalHours = 6 });
        options
            .Setup(o => o.OnChange(It.IsAny<Action<AppSettings, string?>>()))
            .Callback<Action<AppSettings, string?>>(l => listener = l)
            .Returns(subscription.Object);

        using var service = new DatabaseCleanupHostedService(_fileStore.Object, options.Object, _logger.Object);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        subscription.Verify(s => s.Dispose(), Times.Once);
        Assert.NotNull(listener);

        // A change notification that races with shutdown must not schedule a new timer.
        listener(new AppSettings { DatabaseCleanupIntervalHours = 3 }, null);

        VerifyLog(LogLevel.Information, "Database cleanup will run every 6 hours", Times.Once());
        VerifyLog(LogLevel.Information, "Database cleanup will run every 3 hours", Times.Never());
        VerifyLog(LogLevel.Error, "Failed to reconfigure database cleanup timer after settings change", Times.Never());
    }

    [Fact]
    public async Task SettingsChange_WithUnchangedInterval_KeepsTheExistingSchedule()
    {
        var monitor = new TestOptionsMonitor<AppSettings>(new AppSettings { DatabaseCleanupIntervalHours = 6 });
        using var service = new DatabaseCleanupHostedService(_fileStore.Object, monitor, _logger.Object);

        await service.StartAsync(CancellationToken.None);
        monitor.Set(new AppSettings { DatabaseCleanupIntervalHours = 6, MaxWorkers = 7 });

        VerifyLog(LogLevel.Information, "Database cleanup will run every 6 hours", Times.Once());
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_AfterDispose_IgnoresTheDisposedCancellationSource()
    {
        var monitor = new TestOptionsMonitor<AppSettings>(new AppSettings { DatabaseCleanupIntervalHours = 0 });
        var service = new DatabaseCleanupHostedService(_fileStore.Object, monitor, _logger.Object);
        await service.StartAsync(CancellationToken.None);

        service.Dispose();
        var exception = await Record.ExceptionAsync(() => service.StopAsync(CancellationToken.None));

        Assert.Null(exception);
        VerifyLog(LogLevel.Information, "Stopping Database Cleanup Hosted Service", Times.Once());
    }

    [Fact]
    public async Task OverlappingCleanupRun_IsSkippedWhileAnotherIsInProgress()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fileStore
            .Setup(f => f.CleanupStaleEntriesAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                entered.TrySetResult();
                return release.Task;
            });
        var monitor = new TestOptionsMonitor<AppSettings>(new AppSettings { DatabaseCleanupIntervalHours = 0 });
        using var service = new DatabaseCleanupHostedService(_fileStore.Object, monitor, _logger.Object);

        var firstStart = service.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(Timeout);
        Assert.False(firstStart.IsCompleted);

        await service.StartAsync(CancellationToken.None).WaitAsync(Timeout);

        VerifyLog(LogLevel.Debug, "Database cleanup already in progress, skipping this run", Times.Once());
        _fileStore.Verify(f => f.CleanupStaleEntriesAsync(It.IsAny<CancellationToken>()), Times.Once);

        release.SetResult(4);
        await firstStart.WaitAsync(Timeout);

        VerifyLog(LogLevel.Information, "Database cleanup completed, removed 4 stale entries", Times.Once());
        await service.StopAsync(CancellationToken.None);
    }
}
