using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.WebApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers <see cref="ArchiveMetadataScanService"/> (one pass over the files whose cached archive
/// metadata is missing or stale) and <see cref="ArchiveMetadataScanHostedService"/> (when the
/// passes run).
/// </summary>
public sealed class ArchiveMetadataScanServiceTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 10, 0, 0);

    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private List<ArchiveMetadataScanCandidate> _pending = new();

    public ArchiveMetadataScanServiceTests()
    {
        // The store pages through _pending by id, as GetFilesNeedingMetadataScanAsync does.
        _fileStore
            .Setup(f => f.GetFilesNeedingMetadataScanAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int afterId, int max, CancellationToken _) =>
                _pending.Where(c => c.Id > afterId).OrderBy(c => c.Id).Take(max).ToList());
        _fileStore
            .Setup(f => f.RecordScannedArchiveMetadataAsync(It.IsAny<ArchiveMetadataScanCandidate>(), It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _processor
            .Setup(p => p.GetMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, CancellationToken _) => new ComicMetadata { Series = "Saga", Title = Path.GetFileNameWithoutExtension(path) });
    }

    private ArchiveMetadataScanService CreateScanner() =>
        new(_fileStore.Object, _processor.Object, NullLogger<ArchiveMetadataScanService>.Instance);

    private static ArchiveMetadataScanCandidate Candidate(int id, long size = 100) =>
        new(id, $"/library/Saga/Saga {id:000}.cbz", size, T0);

    [Fact]
    public async Task ScanAsync_ReadsAndRecordsEveryPendingFileAcrossPages()
    {
        _pending = Enumerable.Range(1, ArchiveMetadataScanService.BatchSize + 5).Select(id => Candidate(id)).ToList();

        var result = await CreateScanner().ScanAsync(CancellationToken.None);

        Assert.Equal(new ArchiveMetadataScanResult(ArchiveMetadataScanService.BatchSize + 5, 0, 0), result);
        _fileStore.Verify(
            f => f.RecordScannedArchiveMetadataAsync(
                It.Is<ArchiveMetadataScanCandidate>(c => c.Id == 1),
                It.Is<ComicMetadata>(m => m.Title == "Saga 001"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        // Two pages: a full one, then the rest.
        _fileStore.Verify(f => f.GetFilesNeedingMetadataScanAsync(0, ArchiveMetadataScanService.BatchSize, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.GetFilesNeedingMetadataScanAsync(ArchiveMetadataScanService.BatchSize, ArchiveMetadataScanService.BatchSize, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScanAsync_SkipsAnUnreadableArchiveUntilItChanges()
    {
        var damaged = Candidate(1);
        _pending = new List<ArchiveMetadataScanCandidate> { damaged, Candidate(2) };
        _processor.Setup(p => p.GetMetadataAsync(damaged.FilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ComicMetadata?)null);
        var scanner = CreateScanner();

        var first = await scanner.ScanAsync(CancellationToken.None);
        // Still pending (nothing was recorded for it), but not reopened on the next pass.
        _pending = new List<ArchiveMetadataScanCandidate> { damaged };
        var second = await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(new ArchiveMetadataScanResult(1, 1, 0), first);
        Assert.Equal(new ArchiveMetadataScanResult(0, 0, 0), second);
        _processor.Verify(p => p.GetMetadataAsync(damaged.FilePath, It.IsAny<CancellationToken>()), Times.Once);

        // Once the file changes (a new size) it is read again.
        _pending = new List<ArchiveMetadataScanCandidate> { Candidate(1, size: 200) };
        _processor.Setup(p => p.GetMetadataAsync(damaged.FilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicMetadata { Series = "Saga", Title = "Repaired" });
        var third = await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(new ArchiveMetadataScanResult(1, 0, 0), third);
    }

    [Fact]
    public async Task ScanAsync_CountsFilesThatChangedWhileBeingRead()
    {
        _pending = new List<ArchiveMetadataScanCandidate> { Candidate(1) };
        _fileStore
            .Setup(f => f.RecordScannedArchiveMetadataAsync(It.IsAny<ArchiveMetadataScanCandidate>(), It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateScanner().ScanAsync(CancellationToken.None);

        Assert.Equal(new ArchiveMetadataScanResult(0, 0, 1), result);
    }

    [Fact]
    public async Task ScanAsync_Cancelled_StopsBeforeReadingAnotherArchive()
    {
        _pending = Enumerable.Range(1, 3).Select(id => Candidate(id)).ToList();
        using var cts = new CancellationTokenSource();
        _processor
            .Setup(p => p.GetMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ReturnsAsync(new ComicMetadata { Series = "Saga" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateScanner().ScanAsync(cts.Token));

        _processor.Verify(p => p.GetMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HostedService_RunsAPassAtStartupAndAgainWhenTheLibraryChanges()
    {
        var fileStore = _fileStore.As<IChangeVersionSource>();
        long version = 1;
        fileStore.SetupGet(f => f.ChangeVersion).Returns(() => Interlocked.Read(ref version));
        _pending = new List<ArchiveMetadataScanCandidate> { Candidate(1) };
        using var service = new ArchiveMetadataScanHostedService(
            CreateScanner(), _fileStore.Object, NullLogger<ArchiveMetadataScanHostedService>.Instance,
            startupDelay: TimeSpan.Zero, pollInterval: TimeSpan.FromMilliseconds(20));

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => service.CompletedPasses >= 1);
        _processor.Verify(p => p.GetMetadataAsync(Candidate(1).FilePath, It.IsAny<CancellationToken>()), Times.Once);

        // Nothing changed: no further pass reads anything.
        await Task.Delay(200);
        var passes = service.CompletedPasses;
        Assert.Equal(1, passes);

        // A new file arrives and the store's change counter moves on.
        _pending = new List<ArchiveMetadataScanCandidate> { Candidate(2) };
        Interlocked.Increment(ref version);
        await WaitUntilAsync(() => service.CompletedPasses > passes);
        _processor.Verify(p => p.GetMetadataAsync(Candidate(2).FilePath, It.IsAny<CancellationToken>()), Times.Once);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostedService_AFailingPassIsRetriedAndDoesNotStopTheService()
    {
        var fileStore = _fileStore.As<IChangeVersionSource>();
        long version = 1;
        fileStore.SetupGet(f => f.ChangeVersion).Returns(() => Interlocked.Read(ref version));
        _fileStore
            .SetupSequence(f => f.GetFilesNeedingMetadataScanAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"))
            .ReturnsAsync(new List<ArchiveMetadataScanCandidate> { Candidate(1) })
            .ReturnsAsync(new List<ArchiveMetadataScanCandidate>());
        using var service = new ArchiveMetadataScanHostedService(
            CreateScanner(), _fileStore.Object, NullLogger<ArchiveMetadataScanHostedService>.Instance,
            startupDelay: TimeSpan.Zero, pollInterval: TimeSpan.FromMilliseconds(20));

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => service.CompletedPasses >= 1);
        Interlocked.Increment(ref version);
        await WaitUntilAsync(() => service.CompletedPasses >= 2);

        _processor.Verify(p => p.GetMetadataAsync(Candidate(1).FilePath, It.IsAny<CancellationToken>()), Times.Once);
        await service.StopAsync(CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the archive metadata scan.");
            await Task.Delay(10);
        }
    }
}
