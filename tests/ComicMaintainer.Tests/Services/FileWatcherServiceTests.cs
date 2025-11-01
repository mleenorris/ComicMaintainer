using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class FileWatcherServiceTests : IDisposable
{
    private readonly Mock<ILogger<FileWatcherService>> _mockLogger;
    private readonly Mock<IOptions<AppSettings>> _mockOptions;
    private readonly Mock<IFileStoreService> _mockFileStore;
    private readonly Mock<IComicProcessorService> _mockProcessor;
    private readonly AppSettings _settings;
    private readonly string _testDirectory;
    private readonly FileWatcherService _service;

    public FileWatcherServiceTests()
    {
        _mockLogger = new Mock<ILogger<FileWatcherService>>();
        _mockOptions = new Mock<IOptions<AppSettings>>();
        _mockFileStore = new Mock<IFileStoreService>();
        _mockProcessor = new Mock<IComicProcessorService>();

        _testDirectory = Path.Combine(Path.GetTempPath(), $"watcher_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);

        _settings = new AppSettings
        {
            WatchedDirectory = _testDirectory,
            WatcherEnabled = true
        };

        _mockOptions.Setup(o => o.Value).Returns(_settings);

        _service = new FileWatcherService(
            _mockOptions.Object,
            _mockLogger.Object,
            _mockFileStore.Object,
            _mockProcessor.Object);
    }

    [Fact]
    public async Task StartAsync_WhenDirectoryExists_StartsWatcher()
    {
        // Act
        await _service.StartAsync();

        // Assert
        Assert.True(_service.IsRunning);
    }

    [Fact]
    public async Task StartAsync_WhenAlreadyRunning_DoesNotStartAgain()
    {
        // Arrange
        await _service.StartAsync();
        var wasRunning = _service.IsRunning;

        // Act
        await _service.StartAsync();

        // Assert
        Assert.True(wasRunning);
        Assert.True(_service.IsRunning);
    }

    [Fact]
    public async Task StartAsync_WhenDirectoryDoesNotExist_DoesNotStart()
    {
        // Arrange
        _settings.WatchedDirectory = Path.Combine(Path.GetTempPath(), "nonexistent_dir");

        // Act
        await _service.StartAsync();

        // Assert
        Assert.False(_service.IsRunning);
    }

    [Fact]
    public async Task StartAsync_WhenDisabled_DoesNotStart()
    {
        // Arrange - create a new service with disabled watcher
        var disabledSettings = new AppSettings
        {
            WatchedDirectory = _testDirectory,
            WatcherEnabled = false
        };
        var mockDisabledOptions = new Mock<IOptions<AppSettings>>();
        mockDisabledOptions.Setup(o => o.Value).Returns(disabledSettings);
        
        var disabledService = new FileWatcherService(
            mockDisabledOptions.Object,
            _mockLogger.Object,
            _mockFileStore.Object,
            _mockProcessor.Object);

        // Act
        await disabledService.StartAsync();

        // Assert
        Assert.False(disabledService.IsRunning);
    }

    [Fact]
    public async Task StopAsync_WhenRunning_StopsWatcher()
    {
        // Arrange
        await _service.StartAsync();
        Assert.True(_service.IsRunning);

        // Act
        await _service.StopAsync();

        // Assert
        Assert.False(_service.IsRunning);
    }

    [Fact]
    public async Task StopAsync_WhenNotRunning_DoesNotThrow()
    {
        // Act & Assert
        await _service.StopAsync();
        Assert.False(_service.IsRunning);
    }

    [Fact]
    public void SetEnabled_WithTrue_StartsWatcher()
    {
        // Act
        _service.SetEnabled(true);

        // Assert
        Assert.True(_service.IsRunning);
    }

    [Fact]
    public async Task SetEnabled_WithFalse_StopsWatcher()
    {
        // Arrange
        await _service.StartAsync();
        Assert.True(_service.IsRunning);

        // Act
        _service.SetEnabled(false);

        // Assert
        Assert.False(_service.IsRunning);
    }

    [Fact]
    public void IsRunning_WhenNotStarted_ReturnsFalse()
    {
        // Assert
        Assert.False(_service.IsRunning);
    }

    [Fact]
    public async Task StartAsync_ScansExistingFiles()
    {
        // Arrange - Create some test comic files
        var cbzFile = Path.Combine(_testDirectory, "test1.cbz");
        var cbrFile = Path.Combine(_testDirectory, "test2.cbr");
        var txtFile = Path.Combine(_testDirectory, "test.txt"); // Should be ignored
        
        File.WriteAllText(cbzFile, "fake cbz content");
        File.WriteAllText(cbrFile, "fake cbr content");
        File.WriteAllText(txtFile, "fake txt content");

        // Act
        await _service.StartAsync();
        
        // Wait a bit for the async scan to complete
        await Task.Delay(500);

        // Assert - Should add both comic files but not the txt file
        _mockFileStore.Verify(
            fs => fs.AddFileAsync(cbzFile, It.IsAny<CancellationToken>()), 
            Times.Once, 
            "CBZ file should be added during initial scan");
        
        _mockFileStore.Verify(
            fs => fs.AddFileAsync(cbrFile, It.IsAny<CancellationToken>()), 
            Times.Once, 
            "CBR file should be added during initial scan");
        
        _mockFileStore.Verify(
            fs => fs.AddFileAsync(txtFile, It.IsAny<CancellationToken>()), 
            Times.Never, 
            "Non-comic files should not be added");
    }

    [Fact]
    public async Task StartAsync_ScansExistingFilesRecursively()
    {
        // Arrange - Create files in subdirectories
        var subDir = Path.Combine(_testDirectory, "subdir");
        Directory.CreateDirectory(subDir);
        
        var rootFile = Path.Combine(_testDirectory, "root.cbz");
        var subFile = Path.Combine(subDir, "sub.cbr");
        
        File.WriteAllText(rootFile, "fake content");
        File.WriteAllText(subFile, "fake content");

        // Act
        await _service.StartAsync();
        
        // Wait for scan to complete
        await Task.Delay(500);

        // Assert - Should find files in root and subdirectories
        _mockFileStore.Verify(
            fs => fs.AddFileAsync(rootFile, It.IsAny<CancellationToken>()), 
            Times.Once, 
            "Root level file should be found");
        
        _mockFileStore.Verify(
            fs => fs.AddFileAsync(subFile, It.IsAny<CancellationToken>()), 
            Times.Once, 
            "Subdirectory file should be found");
    }

    public void Dispose()
    {
        _service.StopAsync().Wait();
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
