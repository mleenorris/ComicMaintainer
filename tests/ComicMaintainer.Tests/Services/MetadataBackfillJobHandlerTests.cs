using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class MetadataBackfillJobHandlerTests
{
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<ISeriesNameResolver> _resolver = new();

    /// <summary>A write that put the metadata into the archive at its own path.</summary>
    private static readonly Func<string, ComicMetadata, ComicMetadataFieldFlags, CancellationToken, MetadataWriteResult> Written =
        (path, _, _, _) => new MetadataWriteResult(MetadataWriteOutcome.Written, path);


    public MetadataBackfillJobHandlerTests()
    {
        _resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<ComicMetadata?>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesNameResolution { ResolvedSeries = "Resolved Series" });
    }

    private MetadataBackfillJobHandler BuildHandler() => new(
        _fileStore.Object,
        _processor.Object,
        _resolver.Object,
        new Mock<ILogger<MetadataBackfillJobHandler>>().Object);

    [Fact]
    public async Task ExecuteAsync_PicksUpFilesNeedingBackfill_WritesAndMarksVersion()
    {
        var file = new ComicFile
        {
            FilePath = "/library/Series/c001.cbz",
            MetadataVersion = 3,
            WrittenMetadataVersion = 1,
            Metadata = new ComicMetadata { Series = "DB Series", Issue = "1" }
        };
        _fileStore.Setup(f => f.GetFilesNeedingBackfillAsync(200, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { file });
        _processor.Setup(p => p.WriteMetadataAsync(file.FilePath, It.IsAny<ComicMetadata>(), It.IsAny<ComicMetadataFieldFlags>(), It.IsAny<CancellationToken>())).ReturnsAsync(Written);

        var summary = await BuildHandler().ExecuteAsync(null, CancellationToken.None);

        _processor.Verify(p => p.WriteMetadataAsync(file.FilePath,
            It.Is<ComicMetadata>(m => m.Series == "Resolved Series" && m.Issue == "1"),
            It.IsAny<ComicMetadataFieldFlags>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.MarkFileBackfilledAsync(file.FilePath, 3, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("1 files updated", summary);
    }

    [Fact]
    public async Task ExecuteAsync_RunsRegardlessOfWatcherEnableNormalize()
    {
        // The metadata backfill scheduled job must run independently of the
        // deprecated watcher toggles. Previously it short-circuited when
        // WatcherEnableNormalize was false, which prevented it from running
        // for users who have disabled the legacy FileSystemWatcher.
        _fileStore.Setup(f => f.GetFilesNeedingBackfillAsync(200, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ComicFile>());

        var summary = await BuildHandler().ExecuteAsync(null, CancellationToken.None);

        _fileStore.Verify(f => f.GetFilesNeedingBackfillAsync(200, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("0 files updated", summary);
    }

    [Fact]
    public async Task ExecuteAsync_SeriesLocked_IgnoresResolverSuggestion()
    {
        var file = new ComicFile
        {
            FilePath = "/library/Series/c001.cbz",
            MetadataVersion = 2,
            Metadata = new ComicMetadata
            {
                Series = "Locked DB Series",
                UserLockedFieldsMask = (long)ComicMetadataFieldFlags.Series
            }
        };
        _fileStore.Setup(f => f.GetFilesNeedingBackfillAsync(200, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { file });
        _processor.Setup(p => p.WriteMetadataAsync(file.FilePath, It.IsAny<ComicMetadata>(), It.IsAny<ComicMetadataFieldFlags>(), It.IsAny<CancellationToken>())).ReturnsAsync(Written);

        await BuildHandler().ExecuteAsync(null, CancellationToken.None);

        _resolver.Verify(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<ComicMetadata?>(), false, It.IsAny<CancellationToken>()), Times.Never);
        _processor.Verify(p => p.WriteMetadataAsync(file.FilePath,
            It.Is<ComicMetadata>(m => m.Series == "Locked DB Series"),
            It.IsAny<ComicMetadataFieldFlags>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
