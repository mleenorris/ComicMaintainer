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

    // Test delay constants
    private const int WatcherInitDelayMs = 100;  // Time to wait for watcher to initialize
    private const int SimpleEventDelayMs = 500;   // Time to wait for simple file events
    private const int ProcessingDelayMs = 2000;   // Time to wait for stability delay + processing

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
            WatcherEnableRename = true,
            WatcherEnableNormalize = true,
            WatcherFileStabilityDelaySeconds = 1  // Use 1 second for tests
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
        // Arrange - create a new service with disabled watcher (both rename and normalize disabled)
        var disabledSettings = new AppSettings
        {
            WatchedDirectory = _testDirectory,
            WatcherEnableRename = false,
            WatcherEnableNormalize = false
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
    public void SetEnabled_WithTrue_IsDeprecated()
    {
        // SetEnabled is now deprecated and does nothing
        // Act
        #pragma warning disable CS0618 // Type or member is obsolete
        _service.SetEnabled(true);
        #pragma warning restore CS0618 // Type or member is obsolete

        // Assert - Should not start watcher (SetEnabled is now a no-op)
        Assert.False(_service.IsRunning, "SetEnabled is deprecated and should not start watcher");
    }

    [Fact]
    public async Task SetEnabled_WithFalse_IsDeprecated()
    {
        // Arrange
        await _service.StartAsync();
        Assert.True(_service.IsRunning);

        // SetEnabled is now deprecated and does nothing
        // Act
        #pragma warning disable CS0618 // Type or member is obsolete
        _service.SetEnabled(false);
        #pragma warning restore CS0618 // Type or member is obsolete

        // Assert - Should not stop watcher (SetEnabled is now a no-op)
        Assert.True(_service.IsRunning, "SetEnabled is deprecated and should not stop watcher");
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

    [Fact]
    public async Task OnFileRenamed_ProcessesRenamedComicFile()
    {
        // Arrange
        var tempFile = Path.Combine(_testDirectory, ".temp_file.cbz.tmp123");
        var renamedFile = Path.Combine(_testDirectory, "renamed_comic.cbz");
        
        // Setup mock to indicate file is not processed
        _mockFileStore.Setup(fs => fs.IsFileProcessedAsync(renamedFile, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        
        // Create the temporary file
        File.WriteAllText(tempFile, "fake cbz content");
        
        // Start the watcher
        await _service.StartAsync();
        
        // Wait a bit for watcher to initialize
        await Task.Delay(100);

        // Act - Rename the file to a proper comic file name
        File.Move(tempFile, renamedFile);
        
        // Wait for file system events and processing (stability delay + processing time)
        await Task.Delay(2000);

        // Assert - File should be removed from old path and added to new path
        _mockFileStore.Verify(
            fs => fs.RemoveFileAsync(tempFile, It.IsAny<CancellationToken>()), 
            Times.Once, 
            "Old file path should be removed from file store");
        
        _mockFileStore.Verify(
            fs => fs.AddFileAsync(renamedFile, It.IsAny<CancellationToken>()), 
            Times.Once, 
            "New file path should be added to file store");
        
        // Verify that processing status was checked
        _mockFileStore.Verify(
            fs => fs.IsFileProcessedAsync(renamedFile, It.IsAny<CancellationToken>()), 
            Times.Once, 
            "Should check if file is already processed");
        
        // Verify that file was processed
        _mockProcessor.Verify(
            p => p.ProcessFileAsync(renamedFile, It.IsAny<CancellationToken>()), 
            Times.Once, 
            "Renamed file should be processed");
    }

    [Fact]
    public async Task OnFileRenamed_SkipsProcessingIfAlreadyProcessed()
    {
        // Arrange
        var tempFile = Path.Combine(_testDirectory, ".temp_file2.cbz.tmp456");
        var renamedFile = Path.Combine(_testDirectory, "already_processed.cbz");
        
        // Setup mock to indicate file is already processed
        _mockFileStore.Setup(fs => fs.IsFileProcessedAsync(renamedFile, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        
        // Create the temporary file
        File.WriteAllText(tempFile, "fake cbz content");
        
        // Start the watcher
        await _service.StartAsync();
        
        // Wait a bit for watcher to initialize
        await Task.Delay(100);

        // Act - Rename the file
        File.Move(tempFile, renamedFile);
        
        // Wait for file system events
        await Task.Delay(2000);

        // Assert - File should be added to store but NOT processed
        _mockFileStore.Verify(
            fs => fs.AddFileAsync(renamedFile, It.IsAny<CancellationToken>()), 
            Times.Once, 
            "New file path should be added to file store");
        
        _mockFileStore.Verify(
            fs => fs.IsFileProcessedAsync(renamedFile, It.IsAny<CancellationToken>()), 
            Times.Once, 
            "Should check if file is already processed");
        
        // Verify that file was NOT processed since it's already marked as processed
        _mockProcessor.Verify(
            p => p.ProcessFileAsync(renamedFile, It.IsAny<CancellationToken>()), 
            Times.Never, 
            "Already processed file should not be processed again");
    }

    [Fact]
    public async Task OnFileCreated_IgnoresTemporaryFilesStartingWithTmpPrefix()
    {
        // Arrange - Use realistic temporary file pattern matching the issue screenshot
        var tempFile = Path.Combine(_testDirectory, $".tmp_{Guid.NewGuid()}.cbz");
        
        // Start the watcher
        await _service.StartAsync();
        
        // Wait a bit for watcher to initialize
        await Task.Delay(100);

        // Act - Create a temporary file
        File.WriteAllText(tempFile, "fake cbz content");
        
        // Wait for file system events
        await Task.Delay(500);

        // Assert - Temporary file should NOT be added to store
        _mockFileStore.Verify(
            fs => fs.AddFileAsync(tempFile, It.IsAny<CancellationToken>()), 
            Times.Never, 
            "Temporary files should be ignored and not added to file store");
        
        // Verify that file was NOT processed
        _mockProcessor.Verify(
            p => p.ProcessFileAsync(tempFile, It.IsAny<CancellationToken>()), 
            Times.Never, 
            "Temporary files should not be processed");
    }

    [Fact]
    public async Task OnFileCreated_IgnoresFilesWithTmpExtension()
    {
        // Arrange
        var tempFile = Path.Combine(_testDirectory, "myfile.tmp");
        
        // Start the watcher
        await _service.StartAsync();
        
        // Wait a bit for watcher to initialize
        await Task.Delay(100);

        // Act - Create a temporary file with .tmp extension
        File.WriteAllText(tempFile, "fake tmp content");
        
        // Wait for file system events
        await Task.Delay(500);

        // Assert - Temporary file should NOT be added to store or processed
        _mockFileStore.Verify(
            fs => fs.AddFileAsync(tempFile, It.IsAny<CancellationToken>()), 
            Times.Never, 
            ".tmp files should be ignored and not added to file store");
        
        _mockProcessor.Verify(
            p => p.ProcessFileAsync(tempFile, It.IsAny<CancellationToken>()), 
            Times.Never, 
            ".tmp files should not be processed");
    }

    [Fact]
    public async Task OnFileChanged_IgnoresTemporaryFiles()
    {
        // Arrange
        var tempFile = Path.Combine(_testDirectory, ".tmp_test.cbz");
        
        // Create the temporary file
        File.WriteAllText(tempFile, "initial content");
        
        // Start the watcher
        await _service.StartAsync();
        
        // Wait a bit for watcher to initialize
        await Task.Delay(100);

        // Act - Modify the temporary file
        File.AppendAllText(tempFile, "updated content");
        
        // Wait for file system events
        await Task.Delay(500);

        // Assert - Temporary file changes should be ignored
        _mockProcessor.Verify(
            p => p.ProcessFileAsync(tempFile, It.IsAny<CancellationToken>()), 
            Times.Never, 
            "Temporary files should not be processed when changed");
    }

    [Fact]
    public async Task OnFileChanged_SkipsProcessingIfAlreadyProcessed()
    {
        // Arrange
        var comicFile = Path.Combine(_testDirectory, "processed_comic.cbz");
        
        // Setup mock to indicate file is already processed
        _mockFileStore.Setup(fs => fs.IsFileProcessedAsync(comicFile, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        
        // Create the file
        File.WriteAllText(comicFile, "initial content");
        
        // Start the watcher
        await _service.StartAsync();
        
        // Wait a bit for watcher to initialize
        await Task.Delay(WatcherInitDelayMs);

        // Act - Modify the file (simulating website processing it)
        File.AppendAllText(comicFile, "updated content");
        
        // Wait for file system events (stability delay + processing time)
        await Task.Delay(ProcessingDelayMs);

        // Assert - Should check if file is already processed
        _mockFileStore.Verify(
            fs => fs.IsFileProcessedAsync(comicFile, It.IsAny<CancellationToken>()), 
            Times.AtLeastOnce, 
            "Should check if file is already processed");
        
        // Verify that file was NOT processed since it's already marked as processed
        _mockProcessor.Verify(
            p => p.ProcessFileAsync(comicFile, It.IsAny<CancellationToken>()), 
            Times.Never, 
            "Already processed file should not be processed again when changed");
    }

    [Fact]
    public async Task OnFileDeleted_IgnoresTemporaryFiles()
    {
        // Arrange - Use realistic temporary file pattern matching the issue screenshot
        var tempFile = Path.Combine(_testDirectory, $".tmp_{Guid.NewGuid()}.cbz");
        
        // Create the temporary file
        File.WriteAllText(tempFile, "fake cbz content");
        
        // Start the watcher
        await _service.StartAsync();
        
        // Wait a bit for watcher to initialize
        await Task.Delay(100);

        // Act - Delete the temporary file
        File.Delete(tempFile);
        
        // Wait for file system events
        await Task.Delay(500);

        // Assert - Temporary file deletion should be ignored
        _mockFileStore.Verify(
            fs => fs.RemoveFileAsync(tempFile, It.IsAny<CancellationToken>()), 
            Times.Never, 
            "Temporary file deletions should be ignored");
    }

    [Fact]
    public async Task OnFileRenamed_IgnoresWhenTargetIsTemporaryFile()
    {
        // Arrange
        var regularFile = Path.Combine(_testDirectory, "test.cbz");
        var tempFile = Path.Combine(_testDirectory, ".tmp_renamed.cbz");
        
        // Create the regular file
        File.WriteAllText(regularFile, "fake cbz content");
        
        // Start the watcher
        await _service.StartAsync();
        
        // Wait a bit for watcher to initialize
        await Task.Delay(100);

        // Act - Rename to a temporary file
        File.Move(regularFile, tempFile);
        
        // Wait for file system events
        await Task.Delay(500);

        // Assert - Rename to temporary file should be ignored
        _mockFileStore.Verify(
            fs => fs.AddFileAsync(tempFile, It.IsAny<CancellationToken>()), 
            Times.Never, 
            "Rename to temporary file should be ignored");
        
        _mockProcessor.Verify(
            p => p.ProcessFileAsync(tempFile, It.IsAny<CancellationToken>()), 
            Times.Never, 
            "Temporary files should not be processed even after rename");
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
