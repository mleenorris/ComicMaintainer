using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.WebApi.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class DatabaseCleanupHostedServiceTests : IDisposable
{
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

        _service = new DatabaseCleanupHostedService(
            _mockFileStore.Object,
            _mockOptions.Object,
            _mockLogger.Object);
    }

    [Fact]
    public async Task StartAsync_RunsCleanupOnStartup()
    {
        // Arrange
        _mockFileStore
            .Setup(x => x.CleanupStaleEntriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        // Act
        await _service.StartAsync(_cts.Token);

        // Assert
        _mockFileStore.Verify(
            x => x.CleanupStaleEntriesAsync(It.IsAny<CancellationToken>()),
            Times.Once);
        
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Starting Database Cleanup")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task StartAsync_WithIntervalGreaterThanZero_SchedulesPeriodicCleanup()
    {
        // Arrange
        var appSettings = new AppSettings
        {
            DatabaseCleanupIntervalHours = 6
        };
        _mockOptions.Setup(x => x.CurrentValue).Returns(appSettings);
        
        _mockFileStore
            .Setup(x => x.CleanupStaleEntriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // Act
        await _service.StartAsync(_cts.Token);

        // Assert - verify cleanup interval was logged
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("every 6 hours")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task StartAsync_WithIntervalZero_OnlyRunsOnStartup()
    {
        // Arrange
        var appSettings = new AppSettings
        {
            DatabaseCleanupIntervalHours = 0
        };
        _mockOptions.Setup(x => x.CurrentValue).Returns(appSettings);
        
        _mockFileStore
            .Setup(x => x.CleanupStaleEntriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // Act
        await _service.StartAsync(_cts.Token);

        // Assert - verify "run only on startup" was logged
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("run only on startup")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task StartAsync_CleanupRemovesEntries_LogsCount()
    {
        // Arrange
        _mockFileStore
            .Setup(x => x.CleanupStaleEntriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(10);

        // Act
        await _service.StartAsync(_cts.Token);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("removed 10")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task StartAsync_WithCancelledToken_LogsWarning()
    {
        // Arrange
        _mockFileStore
            .Setup(x => x.CleanupStaleEntriesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // Act
        await _service.StartAsync(_cts.Token);

        // Assert - should log warning about cancellation
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("cancelled")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task StartAsync_WithException_LogsError()
    {
        // Arrange
        var testException = new InvalidOperationException("Test error");
        _mockFileStore
            .Setup(x => x.CleanupStaleEntriesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(testException);

        // Act
        await _service.StartAsync(_cts.Token);

        // Assert - should log error but not throw
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Error")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task StopAsync_LogsStoppingMessage()
    {
        // Arrange
        await _service.StartAsync(_cts.Token);

        // Act
        await _service.StopAsync(_cts.Token);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Stopping Database Cleanup")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
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
        await _service.StartAsync(_cts.Token);

        // Act & Assert - should not throw
        _service.Dispose();
    }

    [Fact]
    public async Task StartAsync_WithNegativeInterval_OnlyRunsOnStartup()
    {
        // Arrange
        var appSettings = new AppSettings
        {
            DatabaseCleanupIntervalHours = -1
        };
        _mockOptions.Setup(x => x.CurrentValue).Returns(appSettings);
        
        var service = new DatabaseCleanupHostedService(
            _mockFileStore.Object,
            _mockOptions.Object,
            _mockLogger.Object);

        _mockFileStore
            .Setup(x => x.CleanupStaleEntriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // Act
        await service.StartAsync(_cts.Token);

        // Assert - should log "run only on startup"
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("run only on startup")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
        
        service.Dispose();
    }

    [Fact]
    public async Task StopAsync_MultipleTimes_DoesNotThrow()
    {
        // Arrange
        await _service.StartAsync(_cts.Token);
        await _service.StopAsync(_cts.Token);

        // Act & Assert - calling StopAsync multiple times should not throw
        await _service.StopAsync(_cts.Token);
        await _service.StopAsync(_cts.Token);
    }

    [Fact]
    public async Task StartAsync_LogsScheduledCleanupMessage()
    {
        // Arrange
        _mockFileStore
            .Setup(x => x.CleanupStaleEntriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // Act
        await _service.StartAsync(_cts.Token);

        // Assert - should log starting message
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Starting scheduled database cleanup")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    public void Dispose()
    {
        _cts.Dispose();
        _service.Dispose();
    }
}
