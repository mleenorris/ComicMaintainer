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

    private string CreateArchive(string fileName, string number, string? title = null, bool comicInfo = true)
    {
        var directory = Path.Join(_root, "Saga");
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        if (comicInfo)
        {
            using var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open());
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
    public async Task AFileThatCannotMove_RefusesTheWholeShift_AndNothingIsWritten()
    {
        // Issue 3 cannot take the name issue 1 keeps; nor can a tie-in or an untagged file move.
        var tieIn = CreateArchive("Saga - Chapter 0001.MU.cbz", "1.MU");
        var first = CreateArchive("Saga - Chapter 0001.cbz", "1");
        var untagged = CreateArchive("Saga.cbz", "");
        var third = CreateArchive("Saga - Chapter 0003.cbz", "3");
        var before = Names();
        using var service = CreateService();

        var refused = await Assert.ThrowsAsync<IssueShiftRefusedException>(() => service.ShiftIssueNumbersAsync(new[] { tieIn, first, untagged, third }, -2));

        Assert.Equal(new[]
        {
            "Saga - Chapter 0001.MU.cbz: issue number \"1.MU\" is not a plain number",
            "Saga - Chapter 0001.cbz: #1 would fall below 0",
            "Saga.cbz: it has no issue number",
        }, refused.Problems);
        Assert.Equal("Saga - Chapter 0001.MU.cbz: issue number \"1.MU\" is not a plain number (and 2 more)", refused.Message);
        Assert.Equal(before, Names());
        Assert.Equal("3", Tags("Saga - Chapter 0003.cbz").Element("Number")?.Value);
        Assert.Empty(_jobs.GetJobSummaries());
    }

    [Fact]
    public async Task ANumberAFileLeftOutKeeps_RefusesTheShift()
    {
        var third = CreateArchive("Saga - Chapter 0003.cbz", "3");
        var kept = Path.Join(_root, "Saga", "Saga - Chapter 0001.cbz");
        _fileStore.Setup(s => s.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ComicFile { FilePath = kept, Metadata = new ComicMetadata { Series = "Saga", Issue = "001" } },
            // Another folder's #1, and a duplicate, take no name here.
            new ComicFile { FilePath = Path.Join(_root, "Other", "Other - Chapter 0001.cbz"), Metadata = new ComicMetadata { Issue = "1" } },
            new ComicFile { FilePath = Path.Join(_root, "Saga", "Saga - Chapter 0001 (duplicate).cbz"), IsDuplicate = true, Metadata = new ComicMetadata { Issue = "1" } },
        });
        using var service = CreateService();

        var plan = await service.PlanIssueShiftAsync(new[] { third }, -2);

        Assert.False(plan.CanShift);
        Assert.Equal(new[] { "Saga - Chapter 0003.cbz: #1 is already Saga - Chapter 0001.cbz, which is not being shifted" }, plan.Problems);
    }

    [Fact]
    public async Task TwoFilesEndingOnOneNumber_RefuseTheShift_AndTheOrderFollowsTheDirection()
    {
        var a = CreateArchive("Saga - Chapter 0003.cbz", "3");
        var b = CreateArchive("Saga - Chapter 0003 (2).cbz", "3.0");
        var c = CreateArchive("Saga - Chapter 0004.cbz", "4");
        using var service = CreateService();

        var clash = await service.PlanIssueShiftAsync(new[] { a, b }, -1);
        var down = await service.PlanIssueShiftAsync(new[] { c, a }, -1);
        var up = await service.PlanIssueShiftAsync(new[] { a, c }, 1);

        Assert.Equal(new[] { "Saga - Chapter 0003.cbz, Saga - Chapter 0003 (2).cbz would all be #2" }, clash.Problems);
        Assert.Equal(new[] { a, c }, down.OrderedFiles);
        Assert.Equal(new[] { c, a }, up.OrderedFiles);
    }

    [Fact]
    public async Task AFileWithoutComicInfo_KeepsTheSeriesItWasNamedBy()
    {
        var bare = CreateArchive("Saga 003.cbz", "3", comicInfo: false);
        using var service = CreateService();

        var job = await WaitForJobAsync(await service.ShiftIssueNumbersAsync(new[] { bare }, -2));

        Assert.Equal((1, 0), (job.ProcessedFiles, job.FailedFiles));
        var tags = Tags("Saga - Chapter 0001.cbz");
        Assert.Equal(("Saga", "1"), (tags.Element("Series")?.Value, tags.Element("Number")?.Value));
    }

    [Fact]
    public async Task WithRenamingOff_OnlyTheNumbersChange()
    {
        _settings.WatcherEnableRename = false;
        var files = new[] { 3, 4 }.Select(n => CreateArchive($"Saga - Chapter 000{n}.cbz", n.ToString())).ToList();
        using var service = CreateService();

        var job = await WaitForJobAsync(await service.ShiftIssueNumbersAsync(files, -2));

        Assert.Equal((2, 0), (job.ProcessedFiles, job.FailedFiles));
        Assert.Equal(new[] { "Saga - Chapter 0003.cbz", "Saga - Chapter 0004.cbz" }, Names());
        Assert.Equal("1", Tags("Saga - Chapter 0003.cbz").Element("Number")?.Value);
    }

    [Fact]
    public async Task AfterAFileFails_TheRestAreSkipped_Unwritten()
    {
        var file = CreateArchive("Saga - Chapter 0005.cbz", "5");
        using var service = CreateService();
        var run = new ComicProcessorService.IssueShiftRun(-2) { Stopped = true };

        var result = await service.ShiftIssueNumberAsync(file, run, CancellationToken.None);

        Assert.Equal(new BatchItemResult(false, "Skipped: an issue before it could not be shifted, so its new name may still be taken"), result);
        Assert.Equal("5", Tags("Saga - Chapter 0005.cbz").Element("Number")?.Value);
    }

    [Fact]
    public async Task AFileWhoseNumberChangedSinceThePlan_StopsTheRun()
    {
        var file = CreateArchive("Saga - Chapter 0001.cbz", "1");
        using var service = CreateService();
        var run = new ComicProcessorService.IssueShiftRun(-2);

        var result = await service.ShiftIssueNumberAsync(file, run, CancellationToken.None);

        Assert.Equal(new BatchItemResult(false, "Issue number \"1\" can no longer be shifted by -2"), result);
        Assert.True(run.Stopped);
        Assert.Equal(new BatchItemResult(false, "Its tags could not be read"),
            await service.ShiftIssueNumberAsync(Path.Join(_root, "Saga", "gone.cbz"), new ComicProcessorService.IssueShiftRun(-2), CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ComicProcessorService.MaxIssueNumberShift + 1)]
    [InlineData(-ComicProcessorService.MaxIssueNumberShift - 1)]
    public async Task AShiftOfNothingOrTooFar_IsRefused(int offset)
    {
        using var service = CreateService();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ShiftIssueNumbersAsync(new[] { "/x.cbz" }, offset));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.PlanIssueShiftAsync(new[] { "/x.cbz" }, offset));
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
    [InlineData(".5", 1, "1.5")]
    [InlineData("5.", -1, "4")]
    [InlineData(null, 1, null)]
    public void ShiftedIssue_MovesOnlyAPlainNumber_NeverBelowZero(string? issue, int offset, string? expected)
        => Assert.Equal(expected, ComicProcessorService.ShiftedIssue(issue, offset) is { } shifted ? ComicProcessorService.FormatIssue(shifted) : null);
}
