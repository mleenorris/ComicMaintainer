using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.IO.Compression;

namespace ComicMaintainer.Tests.Services;

public class ComicProcessorServiceTests : IDisposable
{
    private readonly Mock<ILogger<ComicProcessorService>> _mockLogger;
    private readonly Mock<IFileStoreService> _mockFileStore;
    private readonly Mock<IProcessingHistoryService> _mockHistoryService;
    private readonly Mock<IExternalSeriesMetadataService> _mockExternalSeriesMetadata;
    private readonly Mock<IOptionsMonitor<AppSettings>> _mockOptions;
    private readonly AppSettings _settings;
    private readonly string _testDirectory;
    private readonly ComicProcessorService _service;

    public ComicProcessorServiceTests()
    {
        _mockLogger = new Mock<ILogger<ComicProcessorService>>();
        _mockFileStore = new Mock<IFileStoreService>();
        _mockHistoryService = new Mock<IProcessingHistoryService>();
        _mockExternalSeriesMetadata = new Mock<IExternalSeriesMetadataService>();
        _mockOptions = new Mock<IOptionsMonitor<AppSettings>>();
        
        _testDirectory = Path.Combine(Path.GetTempPath(), $"comic_tests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        
        _settings = new AppSettings
        {
            WatchedDirectory = _testDirectory,
            DuplicateDirectory = Path.Combine(_testDirectory, "duplicates"),
            TempFileDirectory = Path.Combine(_testDirectory, "temp"),
            FilenameFormat = "{series} - Chapter {issue}",
            IssueNumberPadding = 4
        };
        
        _mockOptions.Setup(o => o.CurrentValue).Returns(_settings);
        
        _service = new ComicProcessorService(
            _mockOptions.Object,
            _mockLogger.Object,
            _mockFileStore.Object,
            _mockHistoryService.Object,
            externalSeriesMetadata: _mockExternalSeriesMetadata.Object);
    }

    [Fact]
    public async Task ProcessFileAsync_NonExistentFile_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "nonexistent.cbz");

        // Act
        var result = await _service.ProcessFileAsync(filePath);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ProcessFileAsync_NonComicFile_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.txt");
        await File.WriteAllTextAsync(filePath, "test content");

        // Act
        var result = await _service.ProcessFileAsync(filePath);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ProcessFileAsync_ValidComicFile_ReturnsTrue()
    {
        // Arrange
        var filePath = CreateTestComicArchive("Test Series", "1");
        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var result = await _service.ProcessFileAsync(filePath);

        // Assert
        Assert.True(result);
        // Verify that both rename and normalize operations were marked as successful
        _mockFileStore.Verify(f => f.MarkFileRenamedAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Once);
        _mockFileStore.Verify(f => f.MarkFileNormalizedAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetMetadataAsync_FileWithComicInfoXml_ReturnsMetadata()
    {
        // Arrange
        var filePath = CreateTestComicArchive("Batman", "12", year: 2023);

        // Act
        var metadata = await _service.GetMetadataAsync(filePath);

        // Assert
        Assert.NotNull(metadata);
        Assert.Equal("Batman", metadata.Series);
        Assert.Equal("12", metadata.Issue);
        Assert.Equal(2023, metadata.Year);
    }

    [Fact]
    public async Task GetMetadataAsync_NonExistentFile_ReturnsNull()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "nonexistent.cbz");

        // Act
        var metadata = await _service.GetMetadataAsync(filePath);

        // Assert
        Assert.Null(metadata);
    }

    [Fact]
    public async Task UpdateMetadataAsync_ValidFile_ReturnsTrue()
    {
        // Arrange
        var filePath = CreateTestComicArchive("Original Series", "1");
        var newMetadata = new ComicMetadata
        {
            Series = "Updated Series",
            Issue = "2",
            Title = "Updated Title",
            Year = 2024
        };

        // Act
        var result = await _service.UpdateMetadataAsync(filePath, newMetadata);

        // Assert
        Assert.True(result);
        
        // Verify metadata was actually updated
        var updatedMetadata = await _service.GetMetadataAsync(filePath);
        Assert.NotNull(updatedMetadata);
        Assert.Equal("Updated Series", updatedMetadata.Series);
        Assert.Equal("2", updatedMetadata.Issue);
    }

    [Fact]
    public async Task RemoveMetadataAsync_ValidFile_ReturnsTrueAndStripsComicInfo()
    {
        // Arrange
        var filePath = CreateTestComicArchive("Series With Metadata", "5", year: 2024);

        // Sanity-check that the test archive starts out with ComicInfo.xml.
        var before = await _service.GetMetadataAsync(filePath);
        Assert.NotNull(before);
        Assert.Equal("Series With Metadata", before!.Series);

        // Act
        var result = await _service.RemoveMetadataAsync(filePath);

        // Assert
        Assert.True(result);
        Assert.True(File.Exists(filePath));

        // ComicInfo.xml should no longer be present in the archive.
        using (var archive = ZipFile.OpenRead(filePath))
        {
            Assert.DoesNotContain(archive.Entries, e =>
                string.Equals(e.FullName, "ComicInfo.xml", StringComparison.OrdinalIgnoreCase));
            // Other entries are preserved.
            Assert.Contains(archive.Entries, e =>
                string.Equals(e.FullName, "page001.jpg", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task RemoveMetadataAsync_NonExistentFile_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "missing.cbz");

        // Act
        var result = await _service.RemoveMetadataAsync(filePath);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task RemoveMetadataAsync_NonComicFile_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "notes.txt");
        await File.WriteAllTextAsync(filePath, "hello");

        // Act
        var result = await _service.RemoveMetadataAsync(filePath);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task RemoveMetadataAsync_ArchiveWithoutComicInfo_StillReturnsTrue()
    {
        // Arrange
        var filePath = CreateTestComicArchiveNoMetadata("nometa.cbz");

        // Act
        var result = await _service.RemoveMetadataAsync(filePath);

        // Assert — the operation is a no-op for the metadata layer but should
        // still succeed so callers can rely on idempotent behaviour.
        Assert.True(result);
        using var archive = ZipFile.OpenRead(filePath);
        Assert.DoesNotContain(archive.Entries, e =>
            string.Equals(e.FullName, "ComicInfo.xml", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProcessFilesAsync_MultipleFiles_CreatesJob()
    {
        // Arrange
        var file1 = CreateTestComicArchive("Series A", "1");
        var file2 = CreateTestComicArchive("Series B", "2");
        var filePaths = new List<string> { file1, file2 };
        
        _mockFileStore.Setup(f => f.MarkFileProcessedAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var jobId = await _service.ProcessFilesAsync(filePaths);

        // Assert
        Assert.NotEqual(Guid.Empty, jobId);
        
        // Wait for async processing to complete
        var job = await WaitForJobCompletionAsync(jobId);
        
        Assert.NotNull(job);
        Assert.Equal(2, job.TotalFiles);
    }

    [Fact]
    public async Task GetMetadataAsync_FileWithoutComicInfo_ParsesFromFilename()
    {
        // Arrange
        var filePath = CreateTestComicArchiveNoMetadata("Batman Chapter 12.cbz");

        // Act
        var metadata = await _service.GetMetadataAsync(filePath);

        // Assert
        Assert.NotNull(metadata);
        Assert.NotNull(metadata.Series);
        Assert.NotNull(metadata.Issue);
    }

    [Fact]
    public async Task UpdateMetadataAsync_WithLargeFile_HandlesCorrectly()
    {
        // Arrange
        var filePath = CreateTestComicArchive("Test", "1");
        var metadata = new ComicMetadata
        {
            Series = "Updated",
            Issue = "2"
        };

        // Act
        var result = await _service.UpdateMetadataAsync(filePath, metadata);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task ProcessFileAsync_WithDuplicate_MarksAsDuplicate()
    {
        // Arrange
        var series = "Same Series";
        var issue = "1";
        
        // Create first file with the expected target name
        var file1 = CreateTestComicArchiveWithTargetName(series, issue);
        
        // Create second file that will have same target name when renamed
        var file2 = CreateTestComicArchive(series, issue);
        
        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var result = await _service.ProcessFileAsync(file2);

        // Assert
        Assert.True(result);
        // Verify that file2 was marked as duplicate (renamed=false since it couldn't be renamed)
        _mockFileStore.Verify(f => f.MarkFileRenamedAsync(file2, false, It.IsAny<CancellationToken>()), Times.Once);
        // Verify that file2 was marked with IsDuplicate flag
        _mockFileStore.Verify(f => f.MarkFileDuplicateAsync(file2, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_WithCaseOnlyDuplicate_MarksAsDuplicate()
    {
        // Arrange
        var issue = "1";

        CreateTestComicArchiveWithTargetName("Tower Of God", issue);
        var filePath = CreateTestComicArchive("Tower of God", issue);

        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var result = await _service.ProcessFileAsync(filePath);

        // Assert
        Assert.True(result);
        _mockFileStore.Verify(f => f.MarkFileRenamedAsync(filePath, false, It.IsAny<CancellationToken>()), Times.Once);
        _mockFileStore.Verify(f => f.MarkFileDuplicateAsync(filePath, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RenameFileAsync_TargetExists_MarksAsDuplicate()
    {
        // Arrange
        var series = "Test Series";
        var issue = "1";
        
        // Create first file with the expected target name
        var file1 = CreateTestComicArchiveWithTargetName(series, issue);
        
        // Create second file that will have same target name when renamed
        var file2 = CreateTestComicArchive(series, issue);
        
        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var jobId = await _service.RenameFilesAsync(new[] { file2 });
        var job = await WaitForJobCompletionAsync(jobId);

        // Assert
        Assert.NotNull(job);
        Assert.Equal(0, job.ProcessedFiles);
        Assert.Equal(1, job.FailedFiles);
        // Verify file was marked as duplicate (renamed=false)
        _mockFileStore.Verify(f => f.MarkFileRenamedAsync(file2, false, It.IsAny<CancellationToken>()), Times.Once);
        // Verify that file2 was marked with IsDuplicate flag
        _mockFileStore.Verify(f => f.MarkFileDuplicateAsync(file2, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    private string CreateTestComicArchiveWithTargetName(string series, string issue)
    {
        var targetFileName = $"{series} - Chapter {issue.PadLeft(4, '0')}.cbz";
        var filePath = Path.Combine(_testDirectory, targetFileName);
        
        using (var archive = System.IO.Compression.ZipFile.Open(filePath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var comicInfoXml = $@"<?xml version=""1.0""?>
<ComicInfo>
    <Series>{series}</Series>
    <Number>{issue}</Number>
</ComicInfo>";

            var comicInfoEntry = archive.CreateEntry("ComicInfo.xml");
            using (var writer = new StreamWriter(comicInfoEntry.Open()))
            {
                writer.Write(comicInfoXml);
            }
            
            var imageEntry = archive.CreateEntry("page001.jpg");
            using (var writer = new StreamWriter(imageEntry.Open()))
            {
                writer.Write("dummy image content");
            }
        }
        
        return filePath;
    }

    private string CreateTestComicArchiveNoMetadata(string fileName)
    {
        var filePath = Path.Combine(_testDirectory, fileName);

        using (var archive = System.IO.Compression.ZipFile.Open(filePath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var imageEntry = archive.CreateEntry("page001.jpg");
            using (var writer = new StreamWriter(imageEntry.Open()))
            {
                writer.Write("dummy image content");
            }
        }

        return filePath;
    }

    [Fact]
    public void GetJob_NonExistentJob_ReturnsNull()
    {
        // Arrange
        var jobId = Guid.NewGuid();

        // Act
        var job = _service.GetJob(jobId);

        // Assert
        Assert.Null(job);
    }

    [Fact]
    public async Task ProcessFilesAsync_WithEventBroadcaster_BroadcastsEvents()
    {
        // Arrange
        var mockEventBroadcaster = new Mock<IEventBroadcaster>();
        var serviceWithBroadcaster = new ComicProcessorService(
            _mockOptions.Object, 
            _mockLogger.Object, 
            _mockFileStore.Object,
            _mockHistoryService.Object,
            mockEventBroadcaster.Object);

        var file1 = CreateTestComicArchive("Test Series", "1");
        var files = new List<string> { file1 };

        _mockFileStore.Setup(f => f.MarkFileProcessedAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var jobId = await serviceWithBroadcaster.ProcessFilesAsync(files);
        
        // Wait for async processing to complete
        var job = await WaitForJobCompletionAsync(serviceWithBroadcaster, jobId);

        // Assert
        Assert.NotNull(job);
        
        // Verify that event broadcaster was called for job updates
        mockEventBroadcaster.Verify(
            b => b.BroadcastJobUpdateAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>()),
            Times.AtLeastOnce);

        // Verify that file processed event was broadcast
        mockEventBroadcaster.Verify(
            b => b.BroadcastFileProcessedAsync(
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<string?>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task DeleteFilesAsync_DeletesEachFileAndBroadcastsEvents()
    {
        // Arrange
        var mockEventBroadcaster = new Mock<IEventBroadcaster>();
        var service = new ComicProcessorService(
            _mockOptions.Object,
            _mockLogger.Object,
            _mockFileStore.Object,
            _mockHistoryService.Object,
            mockEventBroadcaster.Object);

        var fileA = Path.Combine(_testDirectory, "delete-a.cbz");
        var fileB = Path.Combine(_testDirectory, "delete-b.cbz");
        File.WriteAllText(fileA, "x");
        File.WriteAllText(fileB, "x");

        _mockFileStore
            .Setup(f => f.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var jobId = await service.DeleteFilesAsync(new[] { fileA, fileB });
        var job = await WaitForJobCompletionAsync(service, jobId);

        // Assert
        Assert.NotNull(job);
        Assert.Equal(JobStatus.Completed, job!.Status);
        Assert.Equal(2, job.TotalFiles);
        Assert.Equal(2, job.ProcessedFiles);
        Assert.Equal(0, job.FailedFiles);
        Assert.False(File.Exists(fileA));
        Assert.False(File.Exists(fileB));

        _mockFileStore.Verify(f => f.RemoveFileAsync(fileA, It.IsAny<CancellationToken>()), Times.Once);
        _mockFileStore.Verify(f => f.RemoveFileAsync(fileB, It.IsAny<CancellationToken>()), Times.Once);

        // job_updated must be broadcast at least for Running and Completed.
        mockEventBroadcaster.Verify(
            b => b.BroadcastJobUpdateAsync(
                jobId, It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<int>()),
            Times.AtLeast(2));

        // file_processed should fire once per deleted file.
        mockEventBroadcaster.Verify(
            b => b.BroadcastFileProcessedAsync(
                It.IsAny<string>(),
                true,
                It.IsAny<string?>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task RunCustomBatchJobAsync_InvokesPostLoopHookBeforeCompletion()
    {
        // Arrange
        var mockEventBroadcaster = new Mock<IEventBroadcaster>();
        var service = new ComicProcessorService(
            _mockOptions.Object,
            _mockLogger.Object,
            _mockFileStore.Object,
            _mockHistoryService.Object,
            mockEventBroadcaster.Object);

        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        var postLoopRan = 0;
        var loopCompletedBeforePostHook = false;

        // Act
        var jobId = await service.RunCustomBatchJobAsync(
            operationName: "TestCustomBatch",
            trackedItems: new[] { "a", "b", "c" },
            itemOperation: (item, _) =>
            {
                seen.Add(item);
                return Task.FromResult(true);
            },
            failureMessage: "test failure",
            postLoopAsync: _ =>
            {
                // The per-item loop must have fully drained before the
                // post-loop hook runs.
                loopCompletedBeforePostHook = seen.Count == 3;
                Interlocked.Increment(ref postLoopRan);
                return Task.CompletedTask;
            });

        var job = await WaitForJobCompletionAsync(service, jobId);

        // Assert
        Assert.NotNull(job);
        Assert.Equal(JobStatus.Completed, job!.Status);
        Assert.Equal(3, job.ProcessedFiles);
        Assert.Equal(3, seen.Count);
        Assert.Equal(1, postLoopRan);
        Assert.True(loopCompletedBeforePostHook,
            "post-loop hook must run after the per-item loop has completed");
    }

    private const int JobPollingIntervalMs = 50;

    private async Task<ProcessingJob?> WaitForJobCompletionAsync(Guid jobId, int timeoutMs = 5000)
    {
        return await WaitForJobCompletionAsync(_service, jobId, timeoutMs);
    }

    private static async Task<ProcessingJob?> WaitForJobCompletionAsync(ComicProcessorService service, Guid jobId, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        
        while (!cts.Token.IsCancellationRequested)
        {
            var job = service.GetJob(jobId);
            if (job != null && job.Status != JobStatus.Running && job.Status != JobStatus.Queued)
            {
                return job;
            }
            
            try
            {
                await Task.Delay(JobPollingIntervalMs, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Timeout reached, return current job state
                break;
            }
        }
        
        return service.GetJob(jobId); // Return whatever state we're in after timeout
    }

    private string CreateTestComicArchive(string series, string issue, int? year = null)
    {
        var fileName = $"{series} - Chapter {issue}.cbz";
        var filePath = Path.Combine(_testDirectory, fileName);

        // Create ComicInfo.xml content
        var comicInfoXml = $@"<?xml version=""1.0""?>
<ComicInfo>
    <Series>{series}</Series>
    <Number>{issue}</Number>
    {(year.HasValue ? $"<Year>{year.Value}</Year>" : "")}
    <Title>Test Issue</Title>
    <Publisher>Test Publisher</Publisher>
</ComicInfo>";

        // Create a CBZ (ZIP) archive with ComicInfo.xml
        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            // Add ComicInfo.xml
            var comicInfoEntry = archive.CreateEntry("ComicInfo.xml");
            using (var writer = new StreamWriter(comicInfoEntry.Open()))
            {
                writer.Write(comicInfoXml);
            }

            // Add a dummy image file
            var imageEntry = archive.CreateEntry("page001.jpg");
            using (var writer = new StreamWriter(imageEntry.Open()))
            {
                writer.Write("dummy image content");
            }
        }

        return filePath;
    }

    [Fact]
    public async Task ProcessFileAsync_AddsHistoryEntry_OnSuccess()
    {
        // Arrange
        var file = CreateTestComicArchive("Test Series", "1");
        
        _mockFileStore.Setup(f => f.MarkFileProcessedAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var result = await _service.ProcessFileAsync(file);

        // Assert
        Assert.True(result);
        _mockHistoryService.Verify(
            h => h.AddHistoryEntryAsync(
                It.Is<ProcessingHistoryEntry>(e => 
                    e.Action == "Process" && 
                    e.Success == true &&
                    e.ErrorMessage == null),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ProcessFileAsync_AddsHistoryEntry_OnFileNotFound()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "nonexistent.cbz");

        // Act
        var result = await _service.ProcessFileAsync(filePath);

        // Assert
        Assert.False(result);
        _mockHistoryService.Verify(
            h => h.AddHistoryEntryAsync(
                It.Is<ProcessingHistoryEntry>(e => 
                    e.Action == "Process" && 
                    e.Success == false &&
                    e.ErrorMessage == "File not found"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_AddsHistoryEntry_ForRename()
    {
        // Arrange
        var file = CreateTestComicArchive("Original Series", "1");
        
        // Change metadata to cause rename
        _mockFileStore.Setup(f => f.MarkFileProcessedAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockFileStore.Setup(f => f.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockFileStore.Setup(f => f.AddFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var result = await _service.ProcessFileAsync(file);

        // Assert
        Assert.True(result);
        _mockHistoryService.Verify(
            h => h.AddHistoryEntryAsync(
                It.Is<ProcessingHistoryEntry>(e => 
                    e.Action == "Rename" && 
                    e.Success == true),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task UpdateMetadataAsync_WithFileHandleReleased_SucceedsAfterDelay()
    {
        // Arrange
        var filePath = CreateTestComicArchive("Test Series", "1");
        var newMetadata = new ComicMetadata
        {
            Series = "Updated Series",
            Issue = "1",
            Title = "Updated Title",
            Year = 2024
        };

        // Act
        var result = await _service.UpdateMetadataAsync(filePath, newMetadata);

        // Assert
        Assert.True(result);
        
        // Give a moment for file handles to be fully released
        await Task.Delay(200);
        
        // Verify the file is accessible and metadata was updated
        var updatedMetadata = await _service.GetMetadataAsync(filePath);
        Assert.NotNull(updatedMetadata);
        Assert.Equal("Updated Series", updatedMetadata.Series);
        Assert.Equal("1", updatedMetadata.Issue);
    }

    [Fact]
    public async Task UpdateMetadataAsync_MultipleUpdatesInSequence_AllSucceed()
    {
        // Arrange
        var filePath = CreateTestComicArchive("Original", "1");
        
        // Act & Assert - Multiple updates in sequence should all succeed
        for (int i = 1; i <= 3; i++)
        {
            var metadata = new ComicMetadata
            {
                Series = $"Series {i}",
                Issue = "1",
                Title = $"Title {i}",
                Year = 2024
            };
            
            var result = await _service.UpdateMetadataAsync(filePath, metadata);
            Assert.True(result, $"Update {i} should succeed");
            
            // Brief delay between updates
            await Task.Delay(50);
            
            // Verify metadata was updated
            var updatedMetadata = await _service.GetMetadataAsync(filePath);
            Assert.NotNull(updatedMetadata);
            Assert.Equal($"Series {i}", updatedMetadata.Series);
        }
    }

    [Fact]
    public async Task RenameFileAsync_FileAlreadyHasCorrectName_MarksAsRenamed()
    {
        // Arrange
        // Create a file with the name that matches the template
        var series = "Batman";
        var issue = "0001";
        var fileName = $"{series} - Chapter {issue}.cbz";
        var filePath = Path.Combine(_testDirectory, fileName);
        
        // Create ComicInfo.xml with matching metadata
        var comicInfoXml = $@"<?xml version=""1.0""?>
<ComicInfo>
    <Series>{series}</Series>
    <Number>1</Number>
    <Title>Test Issue</Title>
</ComicInfo>";

        using (var archive = System.IO.Compression.ZipFile.Open(filePath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var comicInfoEntry = archive.CreateEntry("ComicInfo.xml");
            using (var writer = new StreamWriter(comicInfoEntry.Open()))
            {
                writer.Write(comicInfoXml);
            }
            
            var imageEntry = archive.CreateEntry("page001.jpg");
            using (var writer = new StreamWriter(imageEntry.Open()))
            {
                writer.Write("dummy image content");
            }
        }

        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var result = await _service.ProcessFileAsync(filePath);

        // Assert
        Assert.True(result);
        // File should be marked as renamed even though no actual rename occurred
        _mockFileStore.Verify(f => f.MarkFileRenamedAsync(filePath, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NormalizeFileAsync_FileAlreadyNormalized_MarksAsNormalized()
    {
        // Arrange
        // Create a file with existing ComicInfo.xml
        var filePath = CreateTestComicArchive("Manga Series", "5");
        
        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var result = await _service.ProcessFileAsync(filePath);

        // Assert
        Assert.True(result);
        // File should be marked as normalized since it has valid ComicInfo.xml
        _mockFileStore.Verify(f => f.MarkFileNormalizedAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RenameFilesAsync_FileAlreadyHasCorrectName_CompletesSuccessfully()
    {
        // Arrange
        var series = "Spider-Man";
        var issue = "0042";
        var fileName = $"{series} - Chapter {issue}.cbz";
        var filePath = Path.Combine(_testDirectory, fileName);
        
        var comicInfoXml = $@"<?xml version=""1.0""?>
<ComicInfo>
    <Series>{series}</Series>
    <Number>42</Number>
</ComicInfo>";

        using (var archive = System.IO.Compression.ZipFile.Open(filePath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var comicInfoEntry = archive.CreateEntry("ComicInfo.xml");
            using (var writer = new StreamWriter(comicInfoEntry.Open()))
            {
                writer.Write(comicInfoXml);
            }
        }

        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var jobId = await _service.RenameFilesAsync(new[] { filePath });
        var job = await WaitForJobCompletionAsync(jobId);

        // Assert
        Assert.NotNull(job);
        Assert.Equal(1, job.ProcessedFiles);
        Assert.Equal(0, job.FailedFiles);
        _mockFileStore.Verify(f => f.MarkFileRenamedAsync(filePath, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RenameFilesAsync_OnlySeriesTagSet_PreservesChapterNumberInMetadata()
    {
        // Arrange: a file whose ComicInfo.xml has only <Series> set (no
        // <Number> / <Title>), but whose original filename contains an
        // explicit "Chapter <n>" token. After rename, the chapter number
        // and a "Chapter <n>" title should be written into ComicInfo.xml.
        var series = "My Series";
        var originalFileName = $"{series} - Chapter 5.cbz";
        var originalFilePath = Path.Combine(_testDirectory, originalFileName);

        var comicInfoXml = $@"<?xml version=""1.0""?>
<ComicInfo>
    <Series>{series}</Series>
</ComicInfo>";

        using (var archive = ZipFile.Open(originalFilePath, ZipArchiveMode.Create))
        {
            var comicInfoEntry = archive.CreateEntry("ComicInfo.xml");
            using (var writer = new StreamWriter(comicInfoEntry.Open()))
            {
                writer.Write(comicInfoXml);
            }

            var imageEntry = archive.CreateEntry("page001.jpg");
            using (var writer = new StreamWriter(imageEntry.Open()))
            {
                writer.Write("dummy image content");
            }
        }

        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Sanity check: before rename, metadata has only Series set.
        var beforeMetadata = await _service.GetMetadataAsync(originalFilePath);
        Assert.NotNull(beforeMetadata);
        Assert.Equal(series, beforeMetadata!.Series);
        Assert.True(string.IsNullOrEmpty(beforeMetadata.Issue));
        Assert.True(string.IsNullOrEmpty(beforeMetadata.Title));

        // Act
        var jobId = await _service.RenameFilesAsync(new[] { originalFilePath });
        var job = await WaitForJobCompletionAsync(jobId);

        // Assert: job completed successfully.
        Assert.NotNull(job);
        Assert.Equal(1, job!.ProcessedFiles);
        Assert.Equal(0, job.FailedFiles);

        // Locate the renamed file (filename template applies issue padding=4).
        var expectedRenamedFile = Path.Combine(_testDirectory, $"{series} - Chapter 0005.cbz");
        Assert.True(File.Exists(expectedRenamedFile),
            $"Expected renamed file '{expectedRenamedFile}' not found. Files present: " +
            string.Join(", ", Directory.GetFiles(_testDirectory).Select(Path.GetFileName)));

        // Assert: chapter number is now persisted in metadata, and Title is "Chapter 5".
        var afterMetadata = await _service.GetMetadataAsync(expectedRenamedFile);
        Assert.NotNull(afterMetadata);
        Assert.Equal(series, afterMetadata!.Series);
        Assert.Equal("5", afterMetadata.Issue);
        Assert.Equal("Chapter 5", afterMetadata.Title);
    }

    [Fact]
    public async Task NormalizeFilesAsync_FileAlreadyNormalized_CompletesSuccessfully()
    {
        // Arrange
        var filePath = CreateTestComicArchive("Action Comics", "100");
        
        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var jobId = await _service.NormalizeFilesAsync(new[] { filePath });
        var job = await WaitForJobCompletionAsync(jobId);

        // Assert
        Assert.NotNull(job);
        Assert.Equal(1, job.ProcessedFiles);
        Assert.Equal(0, job.FailedFiles);
        _mockFileStore.Verify(f => f.MarkFileNormalizedAsync(filePath, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessFilesAsync_ValidFile_CompletesSuccessfully()
    {
        // Arrange
        var filePath = CreateTestComicArchive("Test Comic", "1");
        
        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var jobId = await _service.ProcessFilesAsync(new[] { filePath });
        var job = await WaitForJobCompletionAsync(jobId);

        // Assert
        Assert.NotNull(job);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(1, job.ProcessedFiles);
        Assert.Equal(0, job.FailedFiles);
    }

    [Fact]
    public async Task CancelJob_RunningJob_CancelsSuccessfully()
    {
        // Arrange - Create multiple files to give us time to cancel
        var files = Enumerable.Range(1, 50)
            .Select(i => CreateTestComicArchive($"Test Comic {i}", i.ToString()))
            .ToList();
        
        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var jobId = await _service.ProcessFilesAsync(files);
        
        // Give the job a brief moment to start processing
        await Task.Delay(50);
        
        // Cancel the job
        var cancelled = _service.CancelJob(jobId);
        
        // Wait for job to finish
        var job = await WaitForJobCompletionAsync(jobId, 10000);

        // Assert
        Assert.True(cancelled);
        Assert.NotNull(job);
        // Job may be completed or cancelled depending on timing, but cancellation should have been attempted
        Assert.True(job.Status == JobStatus.Cancelled || job.Status == JobStatus.Completed);
        if (job.Status == JobStatus.Cancelled)
        {
            Assert.True(job.ProcessedFiles < files.Count, $"Cancelled job should not have processed all files. Processed: {job.ProcessedFiles}/{files.Count}");
        }
    }

    [Fact]
    public async Task GetMetadataAsync_FileWithoutComicInfo_ExtractsSeriesFromFolderName()
    {
        // Arrange - Create a folder with "The Infinite Mage" as the name
        var seriesFolder = Path.Combine(_testDirectory, "The Infinite Mage");
        Directory.CreateDirectory(seriesFolder);
        
        // Create a file in that folder without ComicInfo.xml
        var fileName = "Chapter 12.cbz";
        var filePath = Path.Combine(seriesFolder, fileName);
        
        using (var archive = System.IO.Compression.ZipFile.Open(filePath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var imageEntry = archive.CreateEntry("page001.jpg");
            using (var writer = new StreamWriter(imageEntry.Open()))
            {
                writer.Write("dummy image content");
            }
        }

        // Act
        var metadata = await _service.GetMetadataAsync(filePath);

        // Assert
        Assert.NotNull(metadata);
        Assert.Equal("The Infinite Mage", metadata.Series);
        Assert.Equal("12", metadata.Issue);
    }

    [Fact]
    public async Task GetMetadataAsync_FileWithoutComicInfo_InFolderWithUnderscores_NormalizesSeriesName()
    {
        // Arrange - Create a folder with underscores
        var seriesFolder = Path.Combine(_testDirectory, "Batman_The Dark Knight");
        Directory.CreateDirectory(seriesFolder);
        
        // Create a file in that folder without ComicInfo.xml
        var fileName = "Chapter 5.cbz";
        var filePath = Path.Combine(seriesFolder, fileName);
        
        using (var archive = System.IO.Compression.ZipFile.Open(filePath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var imageEntry = archive.CreateEntry("page001.jpg");
            using (var writer = new StreamWriter(imageEntry.Open()))
            {
                writer.Write("dummy image content");
            }
        }

        // Act
        var metadata = await _service.GetMetadataAsync(filePath);

        // Assert
        Assert.NotNull(metadata);
        // Underscores should be converted to colons by NormalizeSeriesName
        Assert.Equal("Batman:The Dark Knight", metadata.Series);
        Assert.Equal("5", metadata.Issue);
    }

    [Fact]
    public async Task NormalizeFileAsync_PreservesFileSeriesWhenNoExternalMetadata()
    {
        // Arrange - Create a folder with a specific name
        var seriesFolder = Path.Combine(_testDirectory, "Spider-Man");
        Directory.CreateDirectory(seriesFolder);

        // Create a file with ComicInfo.xml that has a series name different
        // from the folder name. With no external metadata available for this
        // series, the existing <Series> value in the file should be preserved
        // rather than being overwritten by the folder-derived name.
        var fileName = "Chapter 5.cbz";
        var filePath = Path.Combine(seriesFolder, fileName);

        var comicInfoXml = @"<?xml version=""1.0""?>
<ComicInfo>
    <Series>Existing Series Name</Series>
    <Number>5</Number>
    <Title>Chapter 5</Title>
</ComicInfo>";

        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            var comicInfoEntry = archive.CreateEntry("ComicInfo.xml");
            using (var writer = new StreamWriter(comicInfoEntry.Open()))
            {
                writer.Write(comicInfoXml);
            }

            var imageEntry = archive.CreateEntry("page001.jpg");
            using (var writer = new StreamWriter(imageEntry.Open()))
            {
                writer.Write("dummy image content");
            }
        }

        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Enable only normalize (not rename) to keep the file path unchanged
        _settings.WatcherEnableRename = false;
        _settings.WatcherEnableNormalize = true;

        // Act
        var result = await _service.ProcessFileAsync(filePath);

        // Assert
        Assert.True(result);

        // Read the metadata back to verify the file's existing series was preserved
        var updatedMetadata = await _service.GetMetadataAsync(filePath);
        Assert.NotNull(updatedMetadata);
        Assert.Equal("Existing Series Name", updatedMetadata.Series);
        Assert.Equal("5", updatedMetadata.Issue);
    }

    [Fact]
    public async Task NormalizeFileAsync_MissingIssueNumber_ParsesFromFilename()
    {
        // Arrange - ComicInfo.xml has no <Number> element, but the filename
        // contains a chapter number. After normalization the issue number
        // should be populated from the filename and the title should become
        // "Chapter <n>" instead of "Chapter Unknown".
        var seriesFolder = Path.Combine(_testDirectory, "Spider-Man");
        Directory.CreateDirectory(seriesFolder);

        var fileName = "Spider-Man - Chapter 7.cbz";
        var filePath = Path.Combine(seriesFolder, fileName);

        var comicInfoXml = @"<?xml version=""1.0""?>
<ComicInfo>
    <Series>Spider-Man</Series>
    <Title>Some Old Title</Title>
</ComicInfo>";

        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            var comicInfoEntry = archive.CreateEntry("ComicInfo.xml");
            using (var writer = new StreamWriter(comicInfoEntry.Open()))
            {
                writer.Write(comicInfoXml);
            }

            var imageEntry = archive.CreateEntry("page001.jpg");
            using (var writer = new StreamWriter(imageEntry.Open()))
            {
                writer.Write("dummy image content");
            }
        }

        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        _settings.WatcherEnableRename = false;
        _settings.WatcherEnableNormalize = true;

        // Act
        var result = await _service.ProcessFileAsync(filePath);

        // Assert
        Assert.True(result);
        _mockFileStore.Verify(f => f.MarkFileNormalizedAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Once);

        var updatedMetadata = await _service.GetMetadataAsync(filePath);
        Assert.NotNull(updatedMetadata);
        Assert.Equal("Spider-Man", updatedMetadata.Series);
        Assert.Equal("7", updatedMetadata.Issue);
        Assert.Equal("Chapter 7", updatedMetadata.Title);
    }

    [Fact]
    public async Task NormalizeFileAsync_WithFolderUnderscores_SetsSeriesNameWithColons()
    {
        // Arrange - Create a folder with underscores
        var seriesFolder = Path.Combine(_testDirectory, "Batman_The Dark Knight");
        Directory.CreateDirectory(seriesFolder);
        
        // Create a file without ComicInfo.xml
        var fileName = "Chapter 10.cbz";
        var filePath = Path.Combine(seriesFolder, fileName);
        
        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            var imageEntry = archive.CreateEntry("page001.jpg");
            using (var writer = new StreamWriter(imageEntry.Open()))
            {
                writer.Write("dummy image content");
            }
        }

        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Enable only normalize (not rename) to keep the file path unchanged
        _settings.WatcherEnableRename = false;
        _settings.WatcherEnableNormalize = true;

        // Act
        var result = await _service.ProcessFileAsync(filePath);

        // Assert
        Assert.True(result);
        
        // Verify the file was normalized
        _mockFileStore.Verify(f => f.MarkFileNormalizedAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Once);
        
        // Read the metadata back to verify series was set from folder name with underscores converted to colons
        var updatedMetadata = await _service.GetMetadataAsync(filePath);
        Assert.NotNull(updatedMetadata);
        Assert.Equal("Batman:The Dark Knight", updatedMetadata.Series);
        Assert.Equal("10", updatedMetadata.Issue);
    }

    [Fact]
    public async Task NormalizeFileAsync_UsesExternalMetadataCanonicalSeries()
    {
        var seriesFolder = Path.Combine(_testDirectory, "The Dark Knight");
        Directory.CreateDirectory(seriesFolder);

        var filePath = Path.Combine(seriesFolder, "Chapter 7.cbz");
        var comicInfoXml = @"<?xml version=""1.0""?>
<ComicInfo>
    <Series>The Dark Knight</Series>
    <Number>7</Number>
    <Title>Chapter 7</Title>
</ComicInfo>";

        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            var comicInfoEntry = archive.CreateEntry("ComicInfo.xml");
            using (var writer = new StreamWriter(comicInfoEntry.Open()))
            {
                writer.Write(comicInfoXml);
            }

            var imageEntry = archive.CreateEntry("page001.jpg");
            using (var writer = new StreamWriter(imageEntry.Open()))
            {
                writer.Write("dummy image content");
            }
        }

        _settings.WatcherEnableRename = false;
        _settings.WatcherEnableNormalize = true;
        _mockExternalSeriesMetadata
            .Setup(service => service.LookupSeriesAsync("The Dark Knight", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman",
                Aliases = new List<string> { "The Dark Knight" },
                Source = "ComicVine"
            });

        var result = await _service.ProcessFileAsync(filePath);

        Assert.True(result);

        var updatedMetadata = await _service.GetMetadataAsync(filePath);
        Assert.NotNull(updatedMetadata);
        Assert.Equal("Batman", updatedMetadata.Series);
    }

    [Fact]
    public async Task NormalizeFileAsync_UserCanonicalRecord_ShortCircuitsExternalLookup()
    {
        var seriesFolder = Path.Combine(_testDirectory, "Batman");
        Directory.CreateDirectory(seriesFolder);

        var filePath = Path.Combine(seriesFolder, "Chapter 7.cbz");
        // Existing metadata has a stale series name from a previous folder; the
        // file lives under a folder named "Batman" which the user has set as
        // the canonical title via folder-combine.
        var comicInfoXml = @"<?xml version=""1.0""?>
<ComicInfo>
    <Series>The Dark Knight</Series>
    <Number>7</Number>
    <Title>Chapter 7</Title>
</ComicInfo>";

        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            var comicInfoEntry = archive.CreateEntry("ComicInfo.xml");
            using (var writer = new StreamWriter(comicInfoEntry.Open()))
            {
                writer.Write(comicInfoXml);
            }
            var imageEntry = archive.CreateEntry("page001.jpg");
            using (var writer = new StreamWriter(imageEntry.Open()))
            {
                writer.Write("dummy");
            }
        }

        _settings.WatcherEnableRename = false;
        _settings.WatcherEnableNormalize = true;

        // External lookup would steer us to a different canonical title, but
        // the user-canonical cache record should win and short-circuit it.
        _mockExternalSeriesMetadata
            .Setup(service => service.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Some External Canonical Title",
                Aliases = new List<string>(),
                Source = "Test"
            });

        var mockSeriesMetadataCache = new Mock<ISeriesMetadataCacheService>();
        mockSeriesMetadataCache.Setup(c => c.NormalizeKey(It.IsAny<string>()))
            .Returns<string>(s => (s ?? string.Empty).ToLowerInvariant());
        mockSeriesMetadataCache
            .Setup(c => c.GetAsync(It.Is<string>(k => k == "batman"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord
            {
                NormalizedKey = "batman",
                CanonicalTitle = "Batman",
                IsUserCanonical = true,
                UserAliases = new List<string> { "The Dark Knight" }
            });
        mockSeriesMetadataCache
            .Setup(c => c.GetAsync(It.Is<string>(k => k != "batman"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesMetadataCacheRecord?)null);

        // Construct a service with the cache wired in so the user-canonical
        // path is exercised.
        using var service = new ComicProcessorService(
            _mockOptions.Object,
            _mockLogger.Object,
            _mockFileStore.Object,
            _mockHistoryService.Object,
            externalSeriesMetadata: _mockExternalSeriesMetadata.Object,
            seriesMetadataCache: mockSeriesMetadataCache.Object);

        var result = await service.ProcessFileAsync(filePath);
        Assert.True(result);

        var updatedMetadata = await service.GetMetadataAsync(filePath);
        Assert.NotNull(updatedMetadata);
        Assert.Equal("Batman", updatedMetadata.Series);

        // The external lookup must NOT have been consulted because the user
        // already declared the canonical title.
        _mockExternalSeriesMetadata.Verify(
            service => service.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NormalizeFileAsync_FolderNameNotInAliases_AddedAsUserAlias()
    {
        // The file lives under a folder named "The Dark Knight" but external
        // lookup steers us to canonical title "Batman". The folder name
        // should be persisted as a user alias on the Batman cache record so
        // future combines/normalizes "remember" it.
        var seriesFolder = Path.Combine(_testDirectory, "The Dark Knight");
        Directory.CreateDirectory(seriesFolder);

        var filePath = Path.Combine(seriesFolder, "Chapter 7.cbz");
        var comicInfoXml = @"<?xml version=""1.0""?>
<ComicInfo>
    <Series>The Dark Knight</Series>
    <Number>7</Number>
    <Title>Chapter 7</Title>
</ComicInfo>";

        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            var comicInfoEntry = archive.CreateEntry("ComicInfo.xml");
            using (var writer = new StreamWriter(comicInfoEntry.Open()))
            {
                writer.Write(comicInfoXml);
            }
            var imageEntry = archive.CreateEntry("page001.jpg");
            using (var writer = new StreamWriter(imageEntry.Open()))
            {
                writer.Write("dummy");
            }
        }

        _settings.WatcherEnableRename = false;
        _settings.WatcherEnableNormalize = true;

        _mockExternalSeriesMetadata
            .Setup(service => service.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman",
                Aliases = new List<string>(),
                Source = "Test"
            });

        var mockSeriesMetadataCache = new Mock<ISeriesMetadataCacheService>();
        mockSeriesMetadataCache.Setup(c => c.NormalizeKey(It.IsAny<string>()))
            .Returns<string>(s => (s ?? string.Empty).ToLowerInvariant());
        // No record exists yet for "Batman", and no user-canonical record
        // for the folder name either - so the external lookup wins.
        mockSeriesMetadataCache
            .Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesMetadataCacheRecord?)null);
        mockSeriesMetadataCache
            .Setup(c => c.SetUserAliasesAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord());

        using var service = new ComicProcessorService(
            _mockOptions.Object,
            _mockLogger.Object,
            _mockFileStore.Object,
            _mockHistoryService.Object,
            externalSeriesMetadata: _mockExternalSeriesMetadata.Object,
            seriesMetadataCache: mockSeriesMetadataCache.Object);

        var result = await service.ProcessFileAsync(filePath);
        Assert.True(result);

        // Folder name should have been persisted as a user alias on "Batman"
        // (with no canonical title override - we're just adding to the
        // existing/empty alias list, not declaring ownership of canonicality).
        // Note: ResolveNormalizedSeriesAsync is invoked both during the
        // "needs normalize" precheck and the actual normalize, so this can
        // fire more than once with the same effective payload.
        mockSeriesMetadataCache.Verify(c => c.SetUserAliasesAsync(
            "Batman",
            It.Is<IEnumerable<string>>(aliases =>
                aliases.Contains("The Dark Knight", StringComparer.OrdinalIgnoreCase)),
            (string?)null,
            It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task NormalizeFileAsync_FolderNameAlreadyInAliases_DoesNotResetAliases()
    {
        // The folder name is already in the user-alias list - we should
        // not re-call SetUserAliasesAsync.
        var seriesFolder = Path.Combine(_testDirectory, "The Dark Knight");
        Directory.CreateDirectory(seriesFolder);

        var filePath = Path.Combine(seriesFolder, "Chapter 7.cbz");
        var comicInfoXml = @"<?xml version=""1.0""?>
<ComicInfo>
    <Series>The Dark Knight</Series>
    <Number>7</Number>
    <Title>Chapter 7</Title>
</ComicInfo>";

        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            var comicInfoEntry = archive.CreateEntry("ComicInfo.xml");
            using (var writer = new StreamWriter(comicInfoEntry.Open()))
            {
                writer.Write(comicInfoXml);
            }
            var imageEntry = archive.CreateEntry("page001.jpg");
            using (var writer = new StreamWriter(imageEntry.Open()))
            {
                writer.Write("dummy");
            }
        }

        _settings.WatcherEnableRename = false;
        _settings.WatcherEnableNormalize = true;

        _mockExternalSeriesMetadata
            .Setup(service => service.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman",
                Aliases = new List<string>(),
                Source = "Test"
            });

        var mockSeriesMetadataCache = new Mock<ISeriesMetadataCacheService>();
        mockSeriesMetadataCache.Setup(c => c.NormalizeKey(It.IsAny<string>()))
            .Returns<string>(s => (s ?? string.Empty).ToLowerInvariant());
        mockSeriesMetadataCache
            .Setup(c => c.GetAsync(It.Is<string>(k => k == "batman"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord
            {
                NormalizedKey = "batman",
                CanonicalTitle = "Batman",
                UserAliases = new List<string> { "The Dark Knight" }
            });
        mockSeriesMetadataCache
            .Setup(c => c.GetAsync(It.Is<string>(k => k != "batman"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesMetadataCacheRecord?)null);

        using var service = new ComicProcessorService(
            _mockOptions.Object,
            _mockLogger.Object,
            _mockFileStore.Object,
            _mockHistoryService.Object,
            externalSeriesMetadata: _mockExternalSeriesMetadata.Object,
            seriesMetadataCache: mockSeriesMetadataCache.Object);

        var result = await service.ProcessFileAsync(filePath);
        Assert.True(result);

        mockSeriesMetadataCache.Verify(c => c.SetUserAliasesAsync(
            It.IsAny<string>(),
            It.IsAny<IEnumerable<string>>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RenameFilesAsync_WhenForceReprocessIsTrue_RenamesAlreadyRenamedFile()
    {
        var filePath = CreateTestComicArchive("Force Series", "3");
        var expectedPath = Path.Combine(_testDirectory, "Force Series - Chapter 0003.cbz");

        _mockFileStore
            .Setup(store => store.IsFileRenamedAsync(filePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockFileStore
            .Setup(store => store.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockFileStore
            .Setup(store => store.AddFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockFileStore
            .Setup(store => store.MarkFileRenamedAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var jobId = await _service.RenameFilesAsync(new[] { filePath }, forceReprocess: true);
        var job = await WaitForJobCompletionAsync(jobId);

        Assert.NotNull(job);
        Assert.Equal(1, job.ProcessedFiles);
        Assert.True(File.Exists(expectedPath));
        Assert.False(File.Exists(filePath));
    }

    public void Dispose()
    {
        // Clean up test directory
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

    // ---------------------------------------------------------------------
    // Preferred-language normalization tests
    // ---------------------------------------------------------------------
    // These verify that ResolveNormalizedSeriesAsync honors the per-series
    // and global default preferred-language settings via the
    // SeriesDisplayTitleResolver — i.e. that on-disk ComicInfo.xml <Series>
    // is rewritten to the localized title (not just the canonical title).
    // The behaviour is exercised indirectly through ProcessFileAsync, which
    // ultimately calls NormalizeMetadataAsync → ResolveNormalizedSeriesAsync.
    // ---------------------------------------------------------------------

    private string CreateLocalizedComicArchive(string folderName, string seriesInMetadata, string issue)
    {
        var seriesFolder = Path.Combine(_testDirectory, folderName);
        Directory.CreateDirectory(seriesFolder);
        var filePath = Path.Combine(seriesFolder, $"Chapter {issue}.cbz");
        var comicInfoXml = $@"<?xml version=""1.0""?>
<ComicInfo>
    <Series>{seriesInMetadata}</Series>
    <Number>{issue}</Number>
    <Title>Chapter {issue}</Title>
</ComicInfo>";
        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            var comicInfoEntry = archive.CreateEntry("ComicInfo.xml");
            using (var writer = new StreamWriter(comicInfoEntry.Open()))
            {
                writer.Write(comicInfoXml);
            }
            var imageEntry = archive.CreateEntry("page001.jpg");
            using (var writer = new StreamWriter(imageEntry.Open()))
            {
                writer.Write("dummy");
            }
        }
        return filePath;
    }

    private Mock<ISeriesMetadataCacheService> BuildLocalizedCache(SeriesMetadataCacheRecord record)
    {
        var mock = new Mock<ISeriesMetadataCacheService>();
        mock.Setup(c => c.NormalizeKey(It.IsAny<string>()))
            .Returns<string>(s => (s ?? string.Empty).ToLowerInvariant());

        // Match any key derived from the canonical title, an alias, or a
        // localized title — that way the lookup succeeds whether we key off
        // the folder name or the value already in ComicInfo.xml.
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            (record.CanonicalTitle ?? string.Empty).ToLowerInvariant()
        };
        foreach (var alias in record.Aliases ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(alias)) keys.Add(alias.ToLowerInvariant());
        }
        foreach (var alias in record.UserAliases ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(alias)) keys.Add(alias.ToLowerInvariant());
        }

        mock.Setup(c => c.GetAsync(It.Is<string>(k => keys.Contains(k)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);
        mock.Setup(c => c.GetAsync(It.Is<string>(k => !keys.Contains(k)), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesMetadataCacheRecord?)null);
        mock.Setup(c => c.SetUserAliasesAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord());
        return mock;
    }

    [Fact]
    public async Task NormalizeFileAsync_PerSeriesPreferredLanguage_WritesLocalizedSeries()
    {
        var filePath = CreateLocalizedComicArchive("One Piece", "One Piece", "1");

        _settings.WatcherEnableRename = false;
        _settings.WatcherEnableNormalize = true;
        _settings.DefaultPreferredLanguage = null;

        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one piece",
            CanonicalTitle = "One Piece",
            LookupStatus = "success",
            PreferredLanguage = "ja",
            LocalizedTitles = new List<LocalizedTitle>
            {
                new("One Piece", "en"),
                new("ワンピース", "ja"),
            }
        };
        var cache = BuildLocalizedCache(record);

        using var service = new ComicProcessorService(
            _mockOptions.Object,
            _mockLogger.Object,
            _mockFileStore.Object,
            _mockHistoryService.Object,
            externalSeriesMetadata: _mockExternalSeriesMetadata.Object,
            seriesMetadataCache: cache.Object);

        var ok = await service.ProcessFileAsync(filePath);
        Assert.True(ok);

        var updated = await service.GetMetadataAsync(filePath);
        Assert.NotNull(updated);
        Assert.Equal("ワンピース", updated!.Series);
    }

    [Fact]
    public async Task NormalizeFileAsync_ManualLookupStatus_HonorsPreferredLanguage()
    {
        // Regression test: a cache record whose LookupStatus is "manual"
        // (set by SetPreferredLanguageAsync / SetUserAliasesAsync when the
        // user takes a direct action on a series that hasn't been externally
        // matched yet) must still be honored when rewriting per-file <Series>.
        // Previously LookupMatchedRecordAsync only accepted "success" /
        // "manual_match" / IsUserCanonical records, so a "manual" record was
        // silently skipped and the file's <Series> never reflected the
        // user's preferred-language choice.
        var filePath = CreateLocalizedComicArchive("One Piece", "One Piece", "1");

        _settings.WatcherEnableRename = false;
        _settings.WatcherEnableNormalize = true;
        _settings.DefaultPreferredLanguage = null;

        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one piece",
            CanonicalTitle = "One Piece",
            LookupStatus = "manual",
            PreferredLanguage = "ja",
            LocalizedTitles = new List<LocalizedTitle>
            {
                new("One Piece", "en"),
                new("ワンピース", "ja"),
            }
        };
        var cache = BuildLocalizedCache(record);

        using var service = new ComicProcessorService(
            _mockOptions.Object,
            _mockLogger.Object,
            _mockFileStore.Object,
            _mockHistoryService.Object,
            externalSeriesMetadata: _mockExternalSeriesMetadata.Object,
            seriesMetadataCache: cache.Object);

        var ok = await service.ProcessFileAsync(filePath);
        Assert.True(ok);

        var updated = await service.GetMetadataAsync(filePath);
        Assert.NotNull(updated);
        Assert.Equal("ワンピース", updated!.Series);

        // External lookup must NOT have been consulted — the "manual" cache
        // record should short-circuit the lookup chain.
        _mockExternalSeriesMetadata.Verify(
            s => s.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NormalizeFileAsync_GlobalDefaultPreferredLanguage_WritesLocalizedSeries()
    {
        var filePath = CreateLocalizedComicArchive("One Piece", "One Piece", "1");

        _settings.WatcherEnableRename = false;
        _settings.WatcherEnableNormalize = true;
        _settings.DefaultPreferredLanguage = "en";

        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one piece",
            CanonicalTitle = "ワンピース",
            LookupStatus = "success",
            PreferredLanguage = null,
            LocalizedTitles = new List<LocalizedTitle>
            {
                new("ワンピース", "ja"),
                new("One Piece", "en"),
            }
        };
        var cache = BuildLocalizedCache(record);

        using var service = new ComicProcessorService(
            _mockOptions.Object,
            _mockLogger.Object,
            _mockFileStore.Object,
            _mockHistoryService.Object,
            externalSeriesMetadata: _mockExternalSeriesMetadata.Object,
            seriesMetadataCache: cache.Object);

        var ok = await service.ProcessFileAsync(filePath);
        Assert.True(ok);

        var updated = await service.GetMetadataAsync(filePath);
        Assert.NotNull(updated);
        Assert.Equal("One Piece", updated!.Series);
    }

    [Fact]
    public async Task NormalizeFileAsync_UserCanonical_BeatsLanguagePreference()
    {
        var filePath = CreateLocalizedComicArchive("One Piece", "One Piece", "1");

        _settings.WatcherEnableRename = false;
        _settings.WatcherEnableNormalize = true;
        _settings.DefaultPreferredLanguage = "ja";

        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one piece",
            CanonicalTitle = "My One Piece",
            IsUserCanonical = true,
            LookupStatus = "manual",
            PreferredLanguage = "ja",
            UserAliases = new List<string> { "One Piece" },
            LocalizedTitles = new List<LocalizedTitle>
            {
                new("ワンピース", "ja"),
                new("One Piece", "en"),
            }
        };
        var cache = BuildLocalizedCache(record);

        using var service = new ComicProcessorService(
            _mockOptions.Object,
            _mockLogger.Object,
            _mockFileStore.Object,
            _mockHistoryService.Object,
            externalSeriesMetadata: _mockExternalSeriesMetadata.Object,
            seriesMetadataCache: cache.Object);

        var ok = await service.ProcessFileAsync(filePath);
        Assert.True(ok);

        var updated = await service.GetMetadataAsync(filePath);
        Assert.NotNull(updated);
        Assert.Equal("My One Piece", updated!.Series);
    }

    [Fact]
    public async Task NormalizeFileAsync_PreferredLanguageWithoutLocalizedTitle_FallsBackToCanonical()
    {
        var filePath = CreateLocalizedComicArchive("Batman", "Batman", "1");

        _settings.WatcherEnableRename = false;
        _settings.WatcherEnableNormalize = true;
        _settings.DefaultPreferredLanguage = null;

        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "batman",
            CanonicalTitle = "Batman",
            LookupStatus = "success",
            // Per-series preference is Korean but there is no Korean
            // localized title on the record — must fall back to canonical.
            PreferredLanguage = "ko",
            LocalizedTitles = new List<LocalizedTitle>
            {
                new("Batman", "en"),
            }
        };
        var cache = BuildLocalizedCache(record);

        using var service = new ComicProcessorService(
            _mockOptions.Object,
            _mockLogger.Object,
            _mockFileStore.Object,
            _mockHistoryService.Object,
            externalSeriesMetadata: _mockExternalSeriesMetadata.Object,
            seriesMetadataCache: cache.Object);

        var ok = await service.ProcessFileAsync(filePath);
        Assert.True(ok);

        var updated = await service.GetMetadataAsync(filePath);
        Assert.NotNull(updated);
        Assert.Equal("Batman", updated!.Series);
    }

    [Fact]
    public async Task NormalizeFileAsync_ExternalLookupOnly_UsesGlobalPreferredLanguage()
    {
        // No cache record exists yet; the external lookup returns a localized
        // title list. The global default preferred language should still be
        // applied to the transient lookup result.
        var filePath = CreateLocalizedComicArchive("ワンピース", "ワンピース", "1");

        _settings.WatcherEnableRename = false;
        _settings.WatcherEnableNormalize = true;
        _settings.DefaultPreferredLanguage = "en";

        _mockExternalSeriesMetadata
            .Setup(s => s.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "ワンピース",
                Source = "Test",
                LocalizedTitles = new List<LocalizedTitle>
                {
                    new("ワンピース", "ja"),
                    new("One Piece", "en"),
                }
            });

        var cache = new Mock<ISeriesMetadataCacheService>();
        cache.Setup(c => c.NormalizeKey(It.IsAny<string>()))
            .Returns<string>(s => (s ?? string.Empty).ToLowerInvariant());
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesMetadataCacheRecord?)null);
        cache.Setup(c => c.SetUserAliasesAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord());

        using var service = new ComicProcessorService(
            _mockOptions.Object,
            _mockLogger.Object,
            _mockFileStore.Object,
            _mockHistoryService.Object,
            externalSeriesMetadata: _mockExternalSeriesMetadata.Object,
            seriesMetadataCache: cache.Object);

        var ok = await service.ProcessFileAsync(filePath);
        Assert.True(ok);

        var updated = await service.GetMetadataAsync(filePath);
        Assert.NotNull(updated);
        Assert.Equal("One Piece", updated!.Series);
    }

    [Fact]
    public async Task NormalizeFileAsync_AlreadyLocalizedSeries_StillFindsRecordViaFolder()
    {
        // Defensive lookup: a previous normalize pass has already written the
        // Japanese localized title into <Series>. The cache record is keyed
        // off the canonical (English) title, but it's still reachable via
        // the folder name. Verify the file isn't lost (the same localized
        // title should remain after another normalize cycle).
        var filePath = CreateLocalizedComicArchive("One Piece", "ワンピース", "1");

        _settings.WatcherEnableRename = false;
        _settings.WatcherEnableNormalize = true;
        _settings.DefaultPreferredLanguage = "ja";

        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one piece",
            CanonicalTitle = "One Piece",
            LookupStatus = "success",
            PreferredLanguage = null,
            LocalizedTitles = new List<LocalizedTitle>
            {
                new("One Piece", "en"),
                new("ワンピース", "ja"),
            }
        };

        // Only the folder-name key resolves; the localized title key returns
        // null — this is the scenario the defensive lookup is designed for.
        var cache = new Mock<ISeriesMetadataCacheService>();
        cache.Setup(c => c.NormalizeKey(It.IsAny<string>()))
            .Returns<string>(s => (s ?? string.Empty).ToLowerInvariant());
        cache.Setup(c => c.GetAsync(It.Is<string>(k => k == "one piece"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);
        cache.Setup(c => c.GetAsync(It.Is<string>(k => k != "one piece"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesMetadataCacheRecord?)null);
        cache.Setup(c => c.SetUserAliasesAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord());

        using var service = new ComicProcessorService(
            _mockOptions.Object,
            _mockLogger.Object,
            _mockFileStore.Object,
            _mockHistoryService.Object,
            externalSeriesMetadata: _mockExternalSeriesMetadata.Object,
            seriesMetadataCache: cache.Object);

        var ok = await service.ProcessFileAsync(filePath);
        Assert.True(ok);

        var updated = await service.GetMetadataAsync(filePath);
        Assert.NotNull(updated);
        // Global default is "ja"; the Japanese localized title should remain.
        Assert.Equal("ワンピース", updated!.Series);

        // External lookup must NOT have been consulted — the defensive
        // lookup found the record via the folder name.
        _mockExternalSeriesMetadata.Verify(
            s => s.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessFileAsync_WithDecimalIssueNumber_RenamesCorrectly()
    {
        // Arrange - Create a comic file with decimal issue number
        var seriesName = "Second Life Ranker";
        var issueNumber = "142.5";
        
        // Create a series directory
        var seriesDir = Path.Combine(_testDirectory, seriesName);
        Directory.CreateDirectory(seriesDir);
        
        // Create a comic file with incorrect name
        var originalFileName = $"{seriesName} - Chapter {issueNumber}.cbz";
        var originalPath = Path.Combine(seriesDir, originalFileName);
        
        // Create ComicInfo.xml with decimal issue
        var comicInfoXml = $@"<?xml version=""1.0""?>
<ComicInfo>
    <Series>{seriesName}</Series>
    <Number>{issueNumber}</Number>
    <Title>Test Issue</Title>
</ComicInfo>";
        
        using (var archive = ZipFile.Open(originalPath, ZipArchiveMode.Create))
        {
            var comicInfoEntry = archive.CreateEntry("ComicInfo.xml");
            using (var writer = new StreamWriter(comicInfoEntry.Open()))
            {
                writer.Write(comicInfoXml);
            }
            
            var imageEntry = archive.CreateEntry("page001.jpg");
            using (var writer = new StreamWriter(imageEntry.Open()))
            {
                writer.Write("dummy image content");
            }
        }
        
        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());
        
        // Act
        var result = await _service.ProcessFileAsync(originalPath);
        
        // Assert
        Assert.True(result);
        
        // The expected filename should have padded issue number: 0142.5
        var expectedFileName = $"{seriesName} - Chapter 0{issueNumber}.cbz";
        var expectedPath = Path.Combine(seriesDir, expectedFileName);
        
        // Verify the file was renamed to the correct name
        _mockFileStore.Verify(f => f.UpdateFilePathAsync(originalPath, expectedPath, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
        _mockFileStore.Verify(f => f.MarkFileRenamedAsync(expectedPath, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_TemplateOmitsIssue_StillPreservesChapterInFilename()
    {
        // Regression: even when the user-configured FilenameFormat omits the
        // {issue} placeholder (e.g. "{series} - {title}"), the chapter number
        // must not be silently dropped from the renamed filename.
        _settings.FilenameFormat = "{series} - {title}";

        var originalPath = CreateTestComicArchive("Batman", "7");
        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        var result = await _service.ProcessFileAsync(originalPath);

        Assert.True(result);

        // The new filename must still contain the chapter number from
        // metadata.Issue; the safety net appends "Chapter 0007" when the
        // template would otherwise have rendered it without the chapter.
        _mockFileStore.Verify(
            f => f.UpdateFilePathAsync(
                originalPath,
                It.Is<string>(p => Path.GetFileName(p).Contains("0007")),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_ComicInfoMissingNumber_PreservesChapterFromOriginalFilename()
    {
        // Regression: ComicInfo.xml is present but <Number> is empty, so
        // metadata.Issue is null. The original filename clearly contains
        // a "Chapter <n>" token, which must be preserved on rename.
        var fileName = "Batman - Chapter 7.cbz";
        var filePath = Path.Combine(_testDirectory, fileName);

        var comicInfoXml = @"<?xml version=""1.0""?>
<ComicInfo>
    <Series>Batman</Series>
    <Title>The Dark Knight</Title>
</ComicInfo>";

        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("ComicInfo.xml");
            using var w = new StreamWriter(entry.Open());
            w.Write(comicInfoXml);
        }

        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        var result = await _service.ProcessFileAsync(filePath);

        Assert.True(result);

        // The "7" chapter from the original filename must survive even though
        // the template's {issue} placeholder rendered to an empty string. The
        // safety net appends a "Chapter <padded>" token so the chapter
        // number cannot be lost.
        _mockFileStore.Verify(
            f => f.MarkFileRenamedAsync(
                It.Is<string>(p => ComicFileProcessor.ChapterNumbersEquivalent(
                    ComicFileProcessor.ParseChapterNumber(Path.GetFileNameWithoutExtension(p)),
                    "7")),
                true,
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ProcessFileAsync_OneShotWithoutChapter_DoesNotInventChapterNumber()
    {
        // A file with no chapter keyword in the original filename and no
        // metadata.Issue (a one-shot) must NOT have a spurious chapter
        // number appended by the safety net.
        _settings.FilenameFormat = "{series}";

        var fileName = "OneShotSeries.cbz";
        var filePath = Path.Combine(_testDirectory, fileName);

        var comicInfoXml = @"<?xml version=""1.0""?>
<ComicInfo>
    <Series>OneShotSeries</Series>
</ComicInfo>";

        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("ComicInfo.xml");
            using var w = new StreamWriter(entry.Open());
            w.Write(comicInfoXml);
        }

        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        var result = await _service.ProcessFileAsync(filePath);

        Assert.True(result);

        // No rename to a different path should occur (file already matches
        // template "OneShotSeries.cbz") and certainly no chapter token should
        // be invented.
        _mockFileStore.Verify(
            f => f.UpdateFilePathAsync(
                It.IsAny<string>(),
                It.Is<string>(p => Path.GetFileNameWithoutExtension(p).Contains("Chapter")),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()),
            Times.Never);
    }
}
