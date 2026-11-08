using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// An archive over the read limits (<see cref="ArchiveEntryReader"/>) fails the one file with a
/// logged reason: reads return nothing, writes change nothing, and a batch moves on to the next file.
/// </summary>
public partial class ComicProcessorServiceCoverageTests
{
    private const string ArchiveRefusedLog = "Refused to read {FilePath}: {Reason}";

    private static string ComicInfoOverTheLimit() =>
        $"<ComicInfo><Series>Huge</Series><Notes>{new string('x', (int)ArchiveEntryReader.MaxComicInfoBytes)}</Notes></ComicInfo>";

    [Fact]
    public async Task GetMetadataAsync_ComicInfoOverTheLimit_ReturnsNullAndLogsWhy()
    {
        var service = CreateService();
        var path = WriteCbz("Series/huge.cbz", ComicInfoOverTheLimit());

        Assert.Null(await service.GetMetadataAsync(path));

        Assert.True(_logger.Has(LogLevel.Warning, ArchiveRefusedLog));
        Assert.False(_logger.Has(LogLevel.Error, "Error reading metadata from {FilePath}"));
    }

    [Fact]
    public async Task GetSeriesMetadataAsync_ComicInfoOverTheLimit_ReturnsNullAndLogsWhy()
    {
        var service = CreateService();
        var path = WriteCbz("Series/huge.cbz", ComicInfoOverTheLimit());

        Assert.Null(await service.GetSeriesMetadataAsync(path));

        Assert.True(_logger.Has(LogLevel.Warning, ArchiveRefusedLog));
    }

    [Fact]
    public async Task UpdateMetadataAsync_ComicInfoOverTheLimit_FailsAndLeavesTheArchiveAlone()
    {
        var service = CreateService();
        var path = WriteCbz("Series/huge.cbz", ComicInfoOverTheLimit());
        var before = await File.ReadAllBytesAsync(path);

        Assert.False(await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.True(_logger.Has(LogLevel.Warning, ArchiveRefusedLog));
    }

    [Fact]
    public async Task ProcessFilesAsync_ARefusedArchive_FailsThatFileAndHandlesTheRest()
    {
        var service = CreateService();
        var refused = WriteCbz("Series/huge.cbz", ComicInfoOverTheLimit());
        var fine = WriteCbz("Series/Series - Chapter 0001.cbz", Xml("Series", "1", "Chapter 1"));

        var job = await RunJobAsync(service, s => s.ProcessFilesAsync(new[] { refused, fine }));

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(2, job.ProcessedFiles + job.FailedFiles);
        Assert.Contains(refused, job.Errors.Keys);
        Assert.DoesNotContain(fine, job.Errors.Keys);
        Assert.True(_logger.Has(LogLevel.Warning, ArchiveRefusedLog));
    }
}
