using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class MetadataBackfillJobHandlerTests
{
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<ISeriesNameResolver> _resolver = new();
    private readonly Mock<IOptionsMonitor<AppSettings>> _options = new();
    private readonly AppSettings _settings = new() { WatcherEnableNormalize = true };

    public MetadataBackfillJobHandlerTests()
    {
        _options.Setup(o => o.CurrentValue).Returns(_settings);
        _resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<ComicMetadata?>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesNameResolution { ResolvedSeries = "Resolved Series" });
    }

    private MetadataBackfillJobHandler BuildHandler() => new(
        _options.Object,
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
        _fileStore.Setup(f => f.GetFilesNeedingBackfillAsync(200, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { file });
        _processor.Setup(p => p.UpdateMetadataAsync(file.FilePath, It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var summary = await BuildHandler().ExecuteAsync(null, CancellationToken.None);

        _processor.Verify(p => p.UpdateMetadataAsync(file.FilePath,
            It.Is<ComicMetadata>(m => m.Series == "Resolved Series" && m.Issue == "1"),
            It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.MarkFileBackfilledAsync(file.FilePath, 3, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("1 files updated", summary);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsWhenNormalizeDisabled()
    {
        _settings.WatcherEnableNormalize = false;

        var summary = await BuildHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Skipped (WatcherEnableNormalize=false)", summary);
        _fileStore.Verify(f => f.GetFilesNeedingBackfillAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
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
        _fileStore.Setup(f => f.GetFilesNeedingBackfillAsync(200, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { file });
        _processor.Setup(p => p.UpdateMetadataAsync(file.FilePath, It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await BuildHandler().ExecuteAsync(null, CancellationToken.None);

        _resolver.Verify(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<ComicMetadata?>(), false, It.IsAny<CancellationToken>()), Times.Never);
        _processor.Verify(p => p.UpdateMetadataAsync(file.FilePath,
            It.Is<ComicMetadata>(m => m.Series == "Locked DB Series"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
