using System.Collections.Concurrent;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// The archive moves and deletes FilesController performs itself (folder combine, single-file
/// delete) take the per-file lock shared with the watcher, the jobs and the metadata edits, so
/// they never land between another operation's read of an archive and its replace or rename.
/// </summary>
/// <remarks>
/// Each test holds a file's lock the way an in-flight operation would, starts the request, waits
/// until the request asks for that lock, and checks that nothing has changed on disk or in the
/// store before the lock is released.
/// </remarks>
public sealed class FilesControllerFileLockTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    private readonly string _root = Path.Join(Path.GetTempPath(), $"fc_lock_{Guid.NewGuid():N}");
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<IBatchJobRegistry> _jobRegistry = new();
    private readonly Mock<ISeriesLibraryService> _seriesLibrary = new();
    private readonly Mock<ISeriesMetadataCacheService> _metadataCache = new();
    private readonly RecordingFileLock _fileLock = new();
    private readonly Mock<ComicMaintainer.Core.Notifications.INotificationPublisher> _notifications = new();

    public FilesControllerFileLockTests()
    {
        Directory.CreateDirectory(_root);
        _metadataCache
            .Setup(c => c.NormalizeKey(It.IsAny<string?>()))
            .Returns((string? title) => title?.Trim().ToLowerInvariant() ?? string.Empty);

        // Run the combine job's items and post-loop hook inline, as the job runner would.
        _jobRegistry.RunCustomBatchJobsInline();
        _processor
            .Setup(p => p.NormalizeAndRenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
    }

    public void Dispose() => TestIO.TryDeleteDirectory(_root);

    [Fact]
    public async Task CombineFolders_MoveWaitsForAnOperationHoldingTheFilesLock()
    {
        var destinationDir = Path.Join(_root, "Foo");
        var sourceDir = Path.Join(_root, "Foo (Old)");
        var existing = CreateComic(destinationDir, "Foo 001.cbz");
        var incoming = CreateComic(sourceDir, "Foo 002.cbz");
        var moved = Path.Join(destinationDir, "Foo 002.cbz");
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Tracked(existing, "Foo"), Tracked(incoming, "Foo Legacy") });
        _seriesLibrary
            .Setup(s => s.GetAllSeriesFolderGroupsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new SeriesFoldersResult
                {
                    Id = "s1",
                    Title = "Foo",
                    Folders = new List<SeriesFolderDto>
                    {
                        new() { Directory = destinationDir, FileCount = 1 },
                        new() { Directory = sourceDir, FileCount = 1 }
                    }
                }
            });

        // The watcher, a job or a metadata edit is rewriting the incoming archive.
        var inFlight = await _fileLock.Inner.AcquireAsync(incoming);
        var combine = CreateController().CombineFolders(new FilesController.CombineFoldersRequest
        {
            GroupKey = "series:s1",
            DestinationDirectory = destinationDir,
            SourceDirectories = new List<string> { sourceDir }
        });

        await WaitUntilAsync(() => _fileLock.Requested.Contains(incoming) || combine.IsCompleted, $"the combine to ask for the lock of {incoming}");
        if (combine.IsCompleted)
        {
            Assert.Fail($"The combine finished without waiting for the lock: {Describe((await combine).Result)}");
        }
        Assert.True(File.Exists(incoming));
        Assert.False(File.Exists(moved));
        _fileStore.Verify(f => f.UpdateFilePathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);

        inFlight.Dispose();

        Assert.IsType<AcceptedResult>((await combine).Result);
        Assert.True(File.Exists(moved));
        Assert.False(File.Exists(incoming));
        _fileStore.Verify(f => f.UpdateFilePathAsync(incoming, moved, It.IsAny<CancellationToken>(), false), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteFile_WaitsForAnOperationHoldingTheFilesLock(bool restfulEndpoint)
    {
        var comic = CreateComic(Path.Join(_root, "Foo"), "Foo 001.cbz");

        var inFlight = await _fileLock.Inner.AcquireAsync(comic);
        var delete = DeleteAsync(CreateController(), comic, restfulEndpoint);

        await WaitUntilAsync(() => _fileLock.Requested.Contains(comic), $"the delete to ask for the lock of {comic}");
        Assert.False(delete.IsCompleted);
        Assert.True(File.Exists(comic));
        _fileStore.Verify(f => f.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        inFlight.Dispose();

        Assert.IsType<OkResult>(await delete);
        Assert.False(File.Exists(comic));
        _fileStore.Verify(f => f.RemoveFileAsync(comic, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteFile_AsksMediaServersToRescan_OnceTheFileIsGone(bool restfulEndpoint)
    {
        var comic = CreateComic(Path.Join(_root, "Foo"), "Foo 001.cbz");
        var stillThereAtTheEvent = true;
        _notifications.Setup(n => n.LibraryChanged()).Callback(() => stillThereAtTheEvent = File.Exists(comic));

        Assert.IsType<OkResult>(await DeleteAsync(CreateController(), comic, restfulEndpoint));

        _notifications.Verify(n => n.LibraryChanged(), Times.Once);
        Assert.False(stillThereAtTheEvent);
    }

    [Fact]
    public async Task DeleteFile_FileRenamedByTheOperationItWaitedFor_ReturnsNotFoundAndKeepsTheRenamedFile()
    {
        var comic = CreateComic(Path.Join(_root, "Foo"), "foo 1.cbz");
        var renamed = Path.Join(_root, "Foo", "Foo 001.cbz");

        var inFlight = await _fileLock.Inner.AcquireAsync(comic);
        var delete = DeleteAsync(CreateController(), comic, restfulEndpoint: true);
        await WaitUntilAsync(() => _fileLock.Requested.Contains(comic), $"the delete to ask for the lock of {comic}");

        // The processing that held the lock renames the archive.
        File.Move(comic, renamed);
        inFlight.Dispose();

        Assert.IsType<NotFoundResult>(await delete);
        Assert.True(File.Exists(renamed));
        _fileStore.Verify(f => f.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _notifications.Verify(n => n.LibraryChanged(), Times.Never);
    }

    [Fact]
    public async Task DeleteFile_RequestAbortedWhileWaitingForTheLock_Returns499AndChangesNothing()
    {
        var comic = CreateComic(Path.Join(_root, "Foo"), "Foo 001.cbz");
        using var aborted = new CancellationTokenSource();

        using (await _fileLock.Inner.AcquireAsync(comic))
        {
            var delete = CreateController().DeleteFileByEncodedPath(Encode(comic), aborted.Token);
            await WaitUntilAsync(() => _fileLock.Requested.Contains(comic), $"the delete to ask for the lock of {comic}");
            aborted.Cancel();

            var result = Assert.IsType<ObjectResult>(await delete);
            Assert.Equal(499, result.StatusCode);
        }

        Assert.True(File.Exists(comic));
        _fileStore.Verify(f => f.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

#pragma warning disable CS0618 // Covers the deprecated /api/delete-file alias too.
    private static Task<ActionResult> DeleteAsync(FilesController controller, string path, bool restfulEndpoint) =>
        restfulEndpoint ? controller.DeleteFileByEncodedPath(Encode(path)) : controller.DeleteFile(path);
#pragma warning restore CS0618

    private FilesController CreateController()
    {
        var settings = new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = _root });
        var history = Mock.Of<IProcessingHistoryService>();
        return new FilesController(
            _fileStore.Object,
            _processor.Object,
            history,
            _seriesLibrary.Object,
            NullLogger<FilesController>.Instance,
            settings,
            TestFolderCombineService.Create(
                _fileStore.Object,
                _processor.Object,
                _jobRegistry.Object,
                history,
                _seriesLibrary.Object,
                settings,
                metadataCache: _metadataCache.Object,
                fileOperationLock: _fileLock),
            Mock.Of<IEventBroadcaster>(),
            Mock.Of<ISeriesNameResolver>(),
            Mock.Of<IScheduledJobRunner>(),
            _fileLock,
            _notifications.Object);
    }

    private static string CreateComic(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, fileName);
        File.WriteAllText(path, "comic");
        return path;
    }

    private static ComicFile Tracked(string path, string series) => new()
    {
        FilePath = path,
        FileName = Path.GetFileName(path),
        Directory = Path.GetDirectoryName(path)!,
        Metadata = new ComicMetadata { Series = series },
        FileSize = 5
    };

    private static string Describe(ActionResult? result) =>
        result is ObjectResult objectResult
            ? $"{objectResult.StatusCode} {System.Text.Json.JsonSerializer.Serialize(objectResult.Value)}"
            : result?.GetType().Name ?? "null";

    private static string Encode(string path) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(path))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out after {WaitTimeout.TotalSeconds:0}s waiting for {description}");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>
    /// The real lock, recording which files the code under test asked for.
    /// </summary>
    private sealed class RecordingFileLock : IFileOperationLock
    {
        public FileOperationLock Inner { get; } = new();

        public ConcurrentBag<string> Requested { get; } = new();

        public ValueTask<IDisposable> AcquireAsync(string filePath, CancellationToken cancellationToken = default)
        {
            Requested.Add(filePath);
            return Inner.AcquireAsync(filePath, cancellationToken);
        }
    }
}
