using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class FileNamingAuditJobHandlerTests
{
    private const string Renamed = "/library/Series/Series - Chapter 001.cbz";
    private const string Unrenamed1 = "/library/Series/c002.cbz";
    private const string Unrenamed2 = "/library/Series/c003.cbz";

    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Guid _renameJobId = Guid.NewGuid();
    private List<string>? _queuedPaths;
    private bool? _queuedWithForce;

    public FileNamingAuditJobHandlerTests()
    {
        SetupTrackedFiles(
            new ComicFile { FilePath = Renamed, IsRenamed = true },
            new ComicFile { FilePath = Unrenamed1, IsRenamed = false },
            new ComicFile { FilePath = Unrenamed2, IsRenamed = false });
        _processor
            .Setup(p => p.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, bool, CancellationToken>((paths, force, _) =>
            {
                _queuedPaths = paths.ToList();
                _queuedWithForce = force;
            })
            .ReturnsAsync(_renameJobId);
    }

    private FileNamingAuditJobHandler CreateHandler() =>
        new(_fileStore.Object, _processor.Object, NullLogger<FileNamingAuditJobHandler>.Instance);

    private void SetupTrackedFiles(params ComicFile[] files) =>
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(files);

    private void SetupRenameJob(JobStatus status, int processed, int failed) =>
        _processor.Setup(p => p.GetJob(_renameJobId)).Returns(new ProcessingJob
        {
            JobId = _renameJobId,
            Status = status,
            ProcessedFiles = processed,
            FailedFiles = failed,
        });

    private void VerifyNothingQueued() =>
        _processor.Verify(p => p.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);

    [Fact]
    public async Task ExecuteAsync_NoTrackedFiles_ReportsEverythingProperlyNamed()
    {
        SetupTrackedFiles();

        var summary = await CreateHandler().ExecuteAsync("""{"autoCorrect":true}""", CancellationToken.None);

        Assert.Equal("Scanned 0: all files already properly named.", summary);
        VerifyNothingQueued();
    }

    [Fact]
    public async Task ExecuteAsync_EveryFileAlreadyRenamed_ReportsEverythingProperlyNamedWithoutQueueing()
    {
        SetupTrackedFiles(
            new ComicFile { FilePath = "/library/A/A - Chapter 001.cbz", IsRenamed = true },
            new ComicFile { FilePath = "/library/A/A - Chapter 002.cbz", IsRenamed = true });

        var summary = await CreateHandler().ExecuteAsync("""{"autoCorrect":true}""", CancellationToken.None);

        Assert.Equal("Scanned 2: all files already properly named.", summary);
        VerifyNothingQueued();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"forceReprocess":false,"autoCorrect":false}""")]
    public async Task ExecuteAsync_ReportOnly_CountsFilesNotYetRenamed(string? optionsJson)
    {
        var summary = await CreateHandler().ExecuteAsync(optionsJson, CancellationToken.None);

        Assert.Equal("Scanned 3: 2 file(s) appear to need renaming. Enable autoCorrect to rename.", summary);
        VerifyNothingQueued();
    }

    [Fact]
    public async Task ExecuteAsync_ForceReprocessReportOnly_CountsEveryFile()
    {
        var summary = await CreateHandler().ExecuteAsync("""{"forceReprocess":true}""", CancellationToken.None);

        Assert.Equal("Scanned 3: 3 file(s) appear to need renaming. Enable autoCorrect to rename.", summary);
        VerifyNothingQueued();
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public async Task ExecuteAsync_AutoCorrect_QueuesUnrenamedFilesAndReportsTheRenameJobOutcome(JobStatus finalStatus)
    {
        SetupRenameJob(finalStatus, processed: 2, failed: 0);

        var summary = await CreateHandler().ExecuteAsync("""{"autoCorrect":true}""", CancellationToken.None);

        Assert.Equal($"Evaluated 2/3 file(s): 2 processed, 0 failed (status: {finalStatus}).", summary);
        Assert.Equal(new[] { Unrenamed1, Unrenamed2 }, _queuedPaths);
        Assert.False(_queuedWithForce);
        _processor.Verify(p => p.GetJob(_renameJobId), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_AutoCorrectWithForceReprocess_QueuesEveryFileWithTheForceFlag()
    {
        SetupRenameJob(JobStatus.Completed, processed: 2, failed: 1);

        // Option names are matched case-insensitively.
        var summary = await CreateHandler().ExecuteAsync("""{"ForceReprocess":true,"AUTOCORRECT":true}""", CancellationToken.None);

        Assert.Equal("Evaluated 3/3 file(s): 2 processed, 1 failed (status: Completed).", summary);
        Assert.Equal(new[] { Renamed, Unrenamed1, Unrenamed2 }, _queuedPaths);
        Assert.True(_queuedWithForce);
    }

    [Fact]
    public async Task ExecuteAsync_RenameJobRecordEvicted_ReportsQueuedCount()
    {
        _processor.Setup(p => p.GetJob(_renameJobId)).Returns((ProcessingJob?)null);

        var summary = await CreateHandler().ExecuteAsync("""{"autoCorrect":true}""", CancellationToken.None);

        Assert.Equal("Queued 2 file(s) for rename evaluation; job record no longer available.", summary);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledWhileWaitingForTheRenameJob_Throws()
    {
        using var cts = new CancellationTokenSource();
        _processor.Setup(p => p.GetJob(_renameJobId))
            .Callback(() => cts.Cancel())
            .Returns(new ProcessingJob { JobId = _renameJobId, Status = JobStatus.Running });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateHandler().ExecuteAsync("""{"autoCorrect":true}""", cts.Token));

        _processor.Verify(p => p.GetJob(_renameJobId), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledWhileQueueingTheRename_ThrowsWithoutPolling()
    {
        using var cts = new CancellationTokenSource();
        _processor
            .Setup(p => p.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ReturnsAsync(_renameJobId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateHandler().ExecuteAsync("""{"autoCorrect":true}""", cts.Token));

        _processor.Verify(p => p.GetJob(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void Metadata_DescribesTheJob()
    {
        var handler = CreateHandler();

        Assert.Equal("file-naming-audit", FileNamingAuditJobHandler.Key);
        Assert.Equal(FileNamingAuditJobHandler.Key, handler.JobKey);
        Assert.Equal("File Naming Audit", handler.DisplayName);
        Assert.Contains("autoCorrect", handler.Description);
        Assert.Equal(
            new ScheduledJobDefaults(Enabled: false, IntervalMinutes: 60 * 24, OptionsJson: """{"forceReprocess":false,"autoCorrect":false}"""),
            handler.Defaults);
    }
}
