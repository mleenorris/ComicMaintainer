using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Compression;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Processing reports success only for a file that ends up fully processed, so a duplicate,
/// an unreadable archive or a failed rename shows up as a failure in jobs and the API.
/// </summary>
public sealed class ComicProcessorServiceProcessingResultTests : IDisposable
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Join(Path.GetTempPath(), $"processing_result_tests_{Guid.NewGuid():N}");
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IProcessingHistoryService> _history = new();
    private readonly AppSettings _settings;

    public ComicProcessorServiceProcessingResultTests()
    {
        Directory.CreateDirectory(_root);
        _settings = new AppSettings
        {
            WatchedDirectory = _root,
            DuplicateDirectory = Path.Join(_root, "duplicates"),
            TempFileDirectory = Path.Join(_root, "temp"),
            FilenameFormat = "{series} - Chapter {issue}",
            IssueNumberPadding = 4,
            WatcherEnableRename = true,
            WatcherEnableNormalize = true
        };
    }

    public void Dispose() => TestIO.TryDeleteDirectory(_root);

    [Fact]
    public async Task ProcessFilesAsync_TargetNameTaken_CountsTheFileAsFailedWithTheReason()
    {
        CreateArchive("Same Series", "Same Series - Chapter 0001.cbz", "Same Series", "1");
        var duplicate = CreateArchive("Same Series", "Same Series 1.cbz", "Same Series", "1");
        using var service = CreateService();

        var job = await RunJobAsync(service, await service.ProcessFilesAsync(new[] { duplicate }));

        Assert.Equal(0, job.ProcessedFiles);
        Assert.Equal(1, job.FailedFiles);
        Assert.Contains("Duplicate", job.Errors[duplicate]);
        _fileStore.Verify(s => s.MarkFileDuplicateAsync(duplicate, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_UnreadableArchive_ReturnsFalse()
    {
        var directory = Path.Join(_root, "Broken");
        Directory.CreateDirectory(directory);
        var corrupt = Path.Join(directory, "Broken - Chapter 0001.cbz");
        await File.WriteAllBytesAsync(corrupt, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        using var service = CreateService();

        Assert.False(await service.ProcessFileAsync(corrupt));
        _fileStore.Verify(s => s.MarkFileNormalizedAsync(corrupt, false, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ProcessFilesAsync_UnreadableArchive_RecordsWhyItFailed()
    {
        var directory = Path.Join(_root, "Broken");
        Directory.CreateDirectory(directory);
        var corrupt = Path.Join(directory, "Broken - Chapter 0001.cbz");
        await File.WriteAllBytesAsync(corrupt, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        using var service = CreateService();

        var job = await RunJobAsync(service, await service.ProcessFilesAsync(new[] { corrupt }));

        Assert.Equal(1, job.FailedFiles);
        Assert.Equal("'Broken - Chapter 0001.cbz' is not a readable ZIP or RAR archive.", job.Errors[corrupt]);
    }

    [Fact]
    public async Task ProcessFileAsync_RenameEnabledButNoSeries_ReturnsFalse()
    {
        // Normalize is off, so nothing supplies a series and the file cannot be renamed.
        _settings.WatcherEnableNormalize = false;
        var directory = Path.Join(_root, "Loose");
        Directory.CreateDirectory(directory);
        var file = Path.Join(directory, "whatever.cbz");
        CreateZip(file, comicInfo: "<?xml version=\"1.0\"?><ComicInfo><Title>No series</Title></ComicInfo>");
        using var service = CreateService();

        Assert.False(await service.ProcessFileAsync(file));
        _fileStore.Verify(s => s.MarkFileRenamedAsync(file, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_FullyProcessed_ReturnsTrue()
    {
        var file = CreateArchive("Saga", "Saga 1.cbz", "Saga", "1");
        using var service = CreateService();

        Assert.True(await service.ProcessFileAsync(file));
        Assert.True(File.Exists(Path.Join(_root, "Saga", "Saga - Chapter 0001.cbz")));
    }

    [Fact]
    public async Task ProcessFileAsync_AlreadyProcessed_StillReportsSuccess()
    {
        var file = CreateArchive("Saga", "Saga - Chapter 0001.cbz", "Saga", "1");
        _fileStore.Setup(s => s.IsFileProcessedAsync(file, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        using var service = CreateService();

        Assert.True(await service.ProcessFileAsync(file));
        _fileStore.Verify(s => s.MarkFileRenamedAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessFileAsync_BothPhasesDisabled_ReportsSuccess()
    {
        // Disabled phases count as done, so a file is "processed" without being changed.
        _settings.WatcherEnableNormalize = false;
        _settings.WatcherEnableRename = false;
        var file = CreateArchive("Saga", "Saga 1.cbz", "Saga", "1");
        using var service = CreateService();

        Assert.True(await service.ProcessFileAsync(file));
        Assert.True(File.Exists(file));
    }

    private BatchJobRegistry? _jobRegistryInstance;

    private BatchJobRegistry Jobs => _jobRegistryInstance ??= TestServices.CreateBatchJobRegistry(
        new TestOptionsMonitor<AppSettings>(_settings),
        NullLogger<BatchJobRegistry>.Instance);

    private ComicProcessorService CreateService() =>
        TestServices.CreateComicProcessor(
            new TestOptionsMonitor<AppSettings>(_settings),
            Mock.Of<ILogger<ComicProcessorService>>(),
            _fileStore.Object,
            _history.Object,
            externalSeriesMetadata: Mock.Of<IExternalSeriesMetadataService>(),
            jobRegistry: Jobs);

    private string CreateArchive(string folder, string fileName, string series, string number)
    {
        var directory = Path.Join(_root, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, fileName);
        CreateZip(path, $"<?xml version=\"1.0\"?><ComicInfo><Series>{series}</Series><Number>{number}</Number><Title>Chapter {number}</Title></ComicInfo>");
        return path;
    }

    private static void CreateZip(string path, string comicInfo)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open()))
        {
            writer.Write(comicInfo);
        }

        using (var writer = new StreamWriter(archive.CreateEntry("page001.jpg").Open()))
        {
            writer.Write("page");
        }
    }

    private async Task<ProcessingJob> RunJobAsync(ComicProcessorService service, Guid jobId)
    {
        var deadline = DateTime.UtcNow + JobTimeout;
        while (true)
        {
            var job = Jobs.GetJob(jobId);
            if (job is { Status: not (JobStatus.Queued or JobStatus.Running) })
            {
                return job;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Job {jobId} did not finish.");
            }

            await Task.Delay(20);
        }
    }
}
