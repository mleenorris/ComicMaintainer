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
    private readonly Mock<IOptions<AppSettings>> _mockOptions;
    private readonly AppSettings _settings;
    private readonly string _testDirectory;
    private readonly ComicProcessorService _service;

    public ComicProcessorServiceTests()
    {
        _mockLogger = new Mock<ILogger<ComicProcessorService>>();
        _mockFileStore = new Mock<IFileStoreService>();
        _mockHistoryService = new Mock<IProcessingHistoryService>();
        _mockOptions = new Mock<IOptions<AppSettings>>();
        
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
        
        _mockOptions.Setup(o => o.Value).Returns(_settings);
        
        _service = new ComicProcessorService(_mockOptions.Object, _mockLogger.Object, _mockFileStore.Object, _mockHistoryService.Object);
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
    public async Task NormalizeFileAsync_SetsSeriesNameFromFolderName()
    {
        // Arrange - Create a folder with a specific name
        var seriesFolder = Path.Combine(_testDirectory, "Spider-Man");
        Directory.CreateDirectory(seriesFolder);
        
        // Create a file with ComicInfo.xml that has wrong series name
        var fileName = "Chapter 5.cbz";
        var filePath = Path.Combine(seriesFolder, fileName);
        
        var comicInfoXml = @"<?xml version=""1.0""?>
<ComicInfo>
    <Series>Wrong Series Name</Series>
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
        
        // Verify the file was normalized
        _mockFileStore.Verify(f => f.MarkFileNormalizedAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Once);
        
        // Read the metadata back to verify series was updated
        var updatedMetadata = await _service.GetMetadataAsync(filePath);
        Assert.NotNull(updatedMetadata);
        Assert.Equal("Spider-Man", updatedMetadata.Series);
        Assert.Equal("5", updatedMetadata.Issue);
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
}
