using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Additional coverage for <see cref="MetadataBackfillJobHandler"/>: per-file failures that must not
/// abort the batch, files without DB metadata, option parsing and cancellation.
/// </summary>
public class MetadataBackfillJobHandlerCoverageTests
{
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<ISeriesNameResolver> _resolver = new();

    public MetadataBackfillJobHandlerCoverageTests()
    {
        _resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<ComicMetadata?>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesNameResolution { ResolvedSeries = "Resolved Series" });
        _processor.Setup(p => p.UpdateMetadataAsync(It.IsAny<string>(), It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        SetupBatch();
    }

    private MetadataBackfillJobHandler CreateHandler() => new(
        _fileStore.Object,
        _processor.Object,
        _resolver.Object,
        NullLogger<MetadataBackfillJobHandler>.Instance);

    private void SetupBatch(params ComicFile[] files) =>
        _fileStore.Setup(f => f.GetFilesNeedingBackfillAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

    private static ComicFile Pending(string path, int version = 2) => new()
    {
        FilePath = path,
        MetadataVersion = version,
        Metadata = new ComicMetadata { Series = "DB Series", Issue = "1" },
    };

    [Fact]
    public async Task ExecuteAsync_PerFileFailures_AreCountedWithoutAbortingTheBatch()
    {
        var rejected = Pending("/library/A/c001.cbz");
        var throwing = Pending("/library/A/c002.cbz");
        var unresolvable = Pending("/library/A/c003.cbz");
        var written = Pending("/library/A/c004.cbz", version: 7);
        SetupBatch(rejected, throwing, unresolvable, written);
        _processor.Setup(p => p.UpdateMetadataAsync(rejected.FilePath, It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _processor.Setup(p => p.UpdateMetadataAsync(throwing.FilePath, It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("archive is read-only"));
        _resolver.Setup(r => r.ResolveAsync(unresolvable.FilePath, It.IsAny<ComicMetadata?>(), false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("resolver offline"));

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Backfill: 1 files updated, 3 failed (queue size was 4).", summary);
        _fileStore.Verify(f => f.MarkFileBackfilledAsync(written.FilePath, 7, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.MarkFileBackfilledAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        _processor.Verify(p => p.UpdateMetadataAsync(unresolvable.FilePath, It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_MarkingTheFileFails_CountsItAsFailed()
    {
        var file = Pending("/library/A/c001.cbz");
        SetupBatch(file);
        _fileStore.Setup(f => f.MarkFileBackfilledAsync(file.FilePath, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("database is locked"));

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Backfill: 0 files updated, 1 failed (queue size was 1).", summary);
    }

    [Fact]
    public async Task ExecuteAsync_FileWithoutDbMetadata_WritesFreshMetadataWithTheResolvedSeries()
    {
        var file = new ComicFile { FilePath = "/library/A/c001.cbz", MetadataVersion = 4, Metadata = null };
        SetupBatch(file);
        ComicMetadata? written = null;
        _processor.Setup(p => p.UpdateMetadataAsync(file.FilePath, It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>()))
            .Callback<string, ComicMetadata, CancellationToken>((_, metadata, _) => written = metadata)
            .ReturnsAsync(true);

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Backfill: 1 files updated, 0 failed (queue size was 1).", summary);
        Assert.NotNull(written);
        Assert.Equal("Resolved Series", written!.Series);
        Assert.Null(written.Issue);
        Assert.Null(written.Title);
        Assert.Empty(written.Authors);
        _resolver.Verify(r => r.ResolveAsync(file.FilePath, It.IsNotNull<ComicMetadata?>(), false, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.MarkFileBackfilledAsync(file.FilePath, 4, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ResolverHasNoSuggestion_KeepsTheDbSeriesAndLeavesTheStoredRowUntouched()
    {
        var file = Pending("/library/A/c001.cbz");
        file.Metadata!.Authors.Add("Writer");
        SetupBatch(file);
        _resolver.Setup(r => r.ResolveAsync(file.FilePath, It.IsAny<ComicMetadata?>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesNameResolution { ResolvedSeries = "   " });
        ComicMetadata? written = null;
        _processor.Setup(p => p.UpdateMetadataAsync(file.FilePath, It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>()))
            .Callback<string, ComicMetadata, CancellationToken>((_, metadata, _) => written = metadata)
            .ReturnsAsync(true);

        await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.NotNull(written);
        Assert.NotSame(file.Metadata, written);
        Assert.Equal("DB Series", written!.Series);
        Assert.Equal("1", written.Issue);
        Assert.Equal(new[] { "Writer" }, written.Authors);
    }

    [Fact]
    public async Task ExecuteAsync_ResolvedSeriesIsWrittenToACopyOfTheStoredMetadata()
    {
        var file = Pending("/library/A/c001.cbz");
        SetupBatch(file);

        await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        _processor.Verify(p => p.UpdateMetadataAsync(
            file.FilePath,
            It.Is<ComicMetadata>(m => m.Series == "Resolved Series" && !ReferenceEquals(m, file.Metadata)),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("DB Series", file.Metadata!.Series);
    }

    [Theory]
    [InlineData(null, 200)]
    [InlineData("", 200)]
    [InlineData("   ", 200)]
    [InlineData("not json", 200)]
    [InlineData("null", 200)]
    [InlineData("{}", 200)]
    [InlineData("""{"maxBatch":5}""", 5)]
    [InlineData("""{"MAXBATCH":7}""", 7)]
    public async Task ExecuteAsync_RequestsABatchOfTheConfiguredSize(string? optionsJson, int expectedBatchSize)
    {
        var summary = await CreateHandler().ExecuteAsync(optionsJson, CancellationToken.None);

        Assert.Equal("Backfill: 0 files updated, 0 failed (queue size was 0).", summary);
        _fileStore.Verify(f => f.GetFilesNeedingBackfillAsync(expectedBatchSize, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledMidBatch_StopsBeforeTheNextFile()
    {
        var first = Pending("/library/A/c001.cbz");
        var second = Pending("/library/A/c002.cbz");
        SetupBatch(first, second);
        using var cts = new CancellationTokenSource();
        _processor.Setup(p => p.UpdateMetadataAsync(first.FilePath, It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ReturnsAsync(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateHandler().ExecuteAsync(null, cts.Token));

        _fileStore.Verify(f => f.MarkFileBackfilledAsync(first.FilePath, first.MetadataVersion, It.IsAny<CancellationToken>()), Times.Once);
        _processor.Verify(p => p.UpdateMetadataAsync(second.FilePath, It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Metadata_DescribesTheJob()
    {
        var handler = CreateHandler();

        Assert.Equal("metadata-backfill", handler.JobKey);
        Assert.Equal("Metadata Backfill", handler.DisplayName);
        Assert.Contains("ComicInfo.xml", handler.Description);
        Assert.Equal(new ScheduledJobDefaults(Enabled: false, IntervalMinutes: 15, OptionsJson: """{"maxBatch":200}"""), handler.Defaults);
    }
}
