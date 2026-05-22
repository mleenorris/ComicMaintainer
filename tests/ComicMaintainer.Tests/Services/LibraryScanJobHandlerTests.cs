using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class LibraryScanJobHandlerTests : IDisposable
{
    private readonly string _watchDir;
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<ISeriesMetadataCacheService> _cache = new();
    private readonly Mock<IOptionsMonitor<AppSettings>> _options = new();
    private readonly AppSettings _settings;

    public LibraryScanJobHandlerTests()
    {
        _watchDir = Path.Combine(Path.GetTempPath(), "lib-scan-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_watchDir);
        _settings = new AppSettings { WatchedDirectory = _watchDir };
        _options.Setup(o => o.CurrentValue).Returns(_settings);
        _cache.Setup(c => c.NormalizeKey(It.IsAny<string?>()))
              .Returns<string?>(s => string.IsNullOrWhiteSpace(s) ? "unknown-series" : s.ToLowerInvariant().Replace(' ', '-'));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchDir, recursive: true); } catch { /* best effort */ }
    }

    private LibraryScanJobHandler BuildHandler() => new(
        _options.Object,
        _fileStore.Object,
        _processor.Object,
        new Mock<ILogger<LibraryScanJobHandler>>().Object,
        _cache.Object);

    private static ProcessingJob CompletedJob(int requested) => new()
    {
        JobId = Guid.NewGuid(),
        Status = JobStatus.Completed,
        TotalFiles = requested,
        ProcessedFiles = requested,
        FailedFiles = 0
    };

    [Fact]
    public async Task ExecuteAsync_AddsNewFilesAndQueuesProcessing()
    {
        var seriesDir = Path.Combine(_watchDir, "One Piece");
        Directory.CreateDirectory(seriesDir);
        var newFile = Path.Combine(seriesDir, "c001.cbz");
        await File.WriteAllBytesAsync(newFile, new byte[] { 0x50, 0x4B });

        _fileStore.Setup(f => f.FileExistsAsync(newFile, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ComicFile>());

        var jobId = Guid.NewGuid();
        _processor.Setup(p => p.NormalizeAndRenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(jobId);
        _processor.Setup(p => p.GetJob(jobId)).Returns(CompletedJob(1));

        var handler = BuildHandler();
        var summary = await handler.ExecuteAsync(null, CancellationToken.None);

        _fileStore.Verify(f => f.AddFileAsync(newFile, It.IsAny<CancellationToken>()), Times.Once);
        _processor.Verify(p => p.NormalizeAndRenameFilesAsync(
            It.Is<IEnumerable<string>>(paths => paths.Contains(newFile)),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("+1 added", summary);
    }

    [Fact]
    public async Task ExecuteAsync_ReconcileDeletions_RemovesGhostFiles()
    {
        // No disk files, but DB has one tracked file -> should be removed.
        var ghostPath = Path.Combine(_watchDir, "Ghost", "missing.cbz");
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ComicFile { FilePath = ghostPath }
        });
        _fileStore.Setup(f => f.FileExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _fileStore.Setup(f => f.GetFilesWithStaleSeriesMetadataAsync(
            It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        var handler = BuildHandler();
        var summary = await handler.ExecuteAsync(null, CancellationToken.None);

        _fileStore.Verify(f => f.RemoveFileAsync(ghostPath, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("-1 removed", summary);
    }

    [Fact]
    public async Task ExecuteAsync_StaleRetag_ForceNormalizesAffectedFiles()
    {
        var seriesDir = Path.Combine(_watchDir, "One Piece");
        Directory.CreateDirectory(seriesDir);
        var trackedFile = Path.Combine(seriesDir, "c001.cbz");
        await File.WriteAllBytesAsync(trackedFile, new byte[] { 0x50, 0x4B });

        _fileStore.Setup(f => f.FileExistsAsync(trackedFile, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ComicFile { FilePath = trackedFile, SeriesMetadataVersion = 1 }
        });
        _cache.Setup(c => c.GetAsync("one-piece", It.IsAny<CancellationToken>()))
              .ReturnsAsync(new SeriesMetadataCacheRecord
              {
                  NormalizedKey = "one-piece",
                  CanonicalTitle = "One Piece",
                  MetadataVersion = 5
              });
        _fileStore.Setup(f => f.GetFilesWithStaleSeriesMetadataAsync(
            It.Is<IEnumerable<string>>(p => p.Contains(trackedFile)), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { trackedFile });

        var jobId = Guid.NewGuid();
        _processor.Setup(p => p.NormalizeFilesAsync(
            It.Is<IEnumerable<string>>(p => p.Contains(trackedFile)), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobId);
        _processor.Setup(p => p.GetJob(jobId)).Returns(CompletedJob(1));

        var handler = BuildHandler();
        var summary = await handler.ExecuteAsync(null, CancellationToken.None);

        _processor.Verify(p => p.NormalizeFilesAsync(
            It.Is<IEnumerable<string>>(p => p.Contains(trackedFile)),
            true,
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("1 stale retagged", summary);
    }

    [Fact]
    public async Task ExecuteAsync_NothingToDo_ReturnsZeroCounts()
    {
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ComicFile>());
        _fileStore.Setup(f => f.GetFilesWithStaleSeriesMetadataAsync(
            It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        var handler = BuildHandler();
        var summary = await handler.ExecuteAsync(null, CancellationToken.None);

        Assert.Contains("+0 added", summary);
        Assert.Contains("-0 removed", summary);
        Assert.Contains("0 stale", summary);
    }

    [Fact]
    public async Task Defaults_AreDisabledWithHourlySchedule()
    {
        var handler = BuildHandler();
        Assert.False(handler.Defaults.Enabled);
        Assert.Equal(60, handler.Defaults.IntervalMinutes);
        Assert.NotNull(handler.Defaults.OptionsJson);
        Assert.Equal("library-scan", handler.JobKey);
    }
}
