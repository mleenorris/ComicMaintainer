using System.IO.Compression;
using System.Xml.Linq;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Shifting the issue numbers of a run a downloader numbered from the wrong start (The
/// Amazing Spider-Man's issue 3 that is #1): each number moves, is locked, and the files are
/// renamed without one taking another's name, on real archives.
/// </summary>
public sealed class ComicProcessorServiceShiftIssuesTests : IDisposable
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Join(Path.GetTempPath(), $"shift_issues_tests_{Guid.NewGuid():N}");
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly AppSettings _settings;
    private readonly BatchJobRegistry _jobs;

    public ComicProcessorServiceShiftIssuesTests()
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
        _jobs = TestServices.CreateBatchJobRegistry(new TestOptionsMonitor<AppSettings>(_settings), NullLogger<BatchJobRegistry>.Instance);
    }

    public void Dispose() => TestIO.TryDeleteDirectory(_root);

    private ComicProcessorService CreateService() =>
        TestServices.CreateComicProcessor(
            new TestOptionsMonitor<AppSettings>(_settings),
            Mock.Of<ILogger<ComicProcessorService>>(),
            _fileStore.Object,
            Mock.Of<IProcessingHistoryService>(),
            externalSeriesMetadata: Mock.Of<IExternalSeriesMetadataService>(),
            jobRegistry: _jobs);

    private string CreateArchive(string fileName, string number, string? title = null)
    {
        var directory = Path.Join(_root, "Saga");
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open()))
        {
            writer.Write($"<?xml version=\"1.0\"?><ComicInfo><Series>Saga</Series><Number>{number}</Number><Title>{title ?? $"Chapter {number}"}</Title></ComicInfo>");
        }

        using (var writer = new StreamWriter(archive.CreateEntry("page001.jpg").Open()))
        {
            writer.Write("page");
        }

        return path;
    }

    private string[] Names() =>
        Directory.EnumerateFiles(Path.Join(_root, "Saga")).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal).ToArray();

    private XElement Tags(string fileName)
    {
        using var archive = ZipFile.OpenRead(Path.Join(_root, "Saga", fileName));
        using var stream = archive.GetEntry("ComicInfo.xml")!.Open();
        return XDocument.Load(stream).Root!;
    }

    private async Task<ProcessingJob> WaitForJobAsync(Guid jobId)
    {
        var deadline = DateTime.UtcNow + JobTimeout;
        while (true)
        {
            if (_jobs.GetJob(jobId) is { Status: not (JobStatus.Queued or JobStatus.Running) } job)
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

    [Fact]
    public async Task ShiftDown_RenumbersRetitlesLocksAndRenames_WithoutADuplicate()
    {
        // Issue 3 is #1: the files a downloader numbered two too high.
        var files = new[] { 3, 4, 5 }.Select(n => CreateArchive($"Saga - Chapter 000{n}.cbz", n.ToString())).ToList();
        using var service = CreateService();

        var job = await WaitForJobAsync(await service.ShiftIssueNumbersAsync(files, -2));

        Assert.Equal((3, 0), (job.ProcessedFiles, job.FailedFiles));
        Assert.Equal(new[] { "Saga - Chapter 0001.cbz", "Saga - Chapter 0002.cbz", "Saga - Chapter 0003.cbz" }, Names());
        Assert.Equal(("1", "Chapter 1"), (Tags("Saga - Chapter 0001.cbz").Element("Number")?.Value, Tags("Saga - Chapter 0001.cbz").Element("Title")?.Value));
        Assert.Equal("3", Tags("Saga - Chapter 0003.cbz").Element("Number")?.Value);
        _fileStore.Verify(s => s.MarkFileDuplicateAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Never);
        _fileStore.Verify(s => s.RecordUserMetadataEditAsync(It.IsAny<string>(), It.Is<ComicMetadata>(m => m.Issue == "1"), ComicMetadataFieldFlags.Issue, false, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(s => s.RecordUserMetadataEditAsync(It.IsAny<string>(), It.IsAny<ComicMetadata>(), ComicMetadataFieldFlags.Issue, false, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task ShiftUp_RenamesTheHighestFirst_WithoutADuplicate()
    {
        var files = new[] { 1, 2, 3 }.Select(n => CreateArchive($"Saga - Chapter 000{n}.cbz", n.ToString())).ToList();
        using var service = CreateService();

        var job = await WaitForJobAsync(await service.ShiftIssueNumbersAsync(files, 2));

        Assert.Equal((3, 0), (job.ProcessedFiles, job.FailedFiles));
        Assert.Equal(new[] { "Saga - Chapter 0003.cbz", "Saga - Chapter 0004.cbz", "Saga - Chapter 0005.cbz" }, Names());
        Assert.Equal("5", Tags("Saga - Chapter 0005.cbz").Element("Number")?.Value);
        _fileStore.Verify(s => s.MarkFileDuplicateAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ARealTitle_IsKept_AndAPaddedOrDecimalNumberMoves()
    {
        var titled = CreateArchive("Saga - Chapter 0012.5.cbz", "012.5", "The Night Gwen Stacy Died");
        using var service = CreateService();

        await WaitForJobAsync(await service.ShiftIssueNumbersAsync(new[] { titled }, -2));

        var tags = Tags("Saga - Chapter 0010.5.cbz");
        Assert.Equal(("10.5", "The Night Gwen Stacy Died"), (tags.Element("Number")?.Value, tags.Element("Title")?.Value));
    }

    [Fact]
    public async Task ANumberThatIsNotPlainOrWouldFallBelowZero_FailsThatFile_AndIsLeftAsItIs()
    {
        var tieIn = CreateArchive("Saga - Chapter 0001.MU.cbz", "1.MU");
        var first = CreateArchive("Saga - Chapter 0001.cbz", "1");
        var untagged = CreateArchive("Saga.cbz", "");
        var third = CreateArchive("Saga - Chapter 0003.cbz", "3");
        using var service = CreateService();

        var job = await WaitForJobAsync(await service.ShiftIssueNumbersAsync(new[] { tieIn, first, untagged, third }, -2));

        Assert.Equal((1, 3), (job.ProcessedFiles, job.FailedFiles));
        Assert.Equal("Issue number \"1.MU\" cannot be shifted by -2", job.Errors[tieIn]);
        Assert.Equal("Issue number \"1\" cannot be shifted by -2", job.Errors[first]);
        Assert.Equal("The file has no issue number", job.Errors[untagged]);
        Assert.Equal("1", Tags("Saga - Chapter 0001.cbz").Element("Number")?.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ComicProcessorService.MaxIssueNumberShift + 1)]
    [InlineData(-ComicProcessorService.MaxIssueNumberShift - 1)]
    public async Task AShiftOfNothingOrTooFar_IsRefused(int offset)
    {
        using var service = CreateService();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ShiftIssueNumbersAsync(new[] { "/x.cbz" }, offset));
    }

    [Theory]
    [InlineData("003", -2, "1")]
    [InlineData("12.5", -2, "10.5")]
    [InlineData(" 7 ", 3, "10")]
    [InlineData("2", -2, "0")]
    [InlineData("1", -2, null)]
    [InlineData("1.MU", 1, null)]
    [InlineData("Annual 1", 1, null)]
    [InlineData("-1", 1, null)]
    [InlineData(null, 1, null)]
    public void ShiftedIssue_MovesOnlyAPlainNumber_NeverBelowZero(string? issue, int offset, string? expected)
        => Assert.Equal(expected, ComicProcessorService.ShiftedIssue(issue, offset) is { } shifted ? ComicProcessorService.FormatIssue(shifted) : null);
}
