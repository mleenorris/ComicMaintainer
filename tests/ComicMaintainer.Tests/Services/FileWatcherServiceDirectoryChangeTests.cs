using System.Collections.Concurrent;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// The watcher keeps the store in step when a whole directory is renamed, moved or deleted
/// outside the application: the OS reports only the directory, never the files under it.
/// </summary>
/// <remarks>
/// Driven by real file-system events in a unique temp directory, against a mocked store and
/// processor. Every wait polls for a recorded call or log entry, bounded by
/// <see cref="WaitTimeout"/>; there are no fixed sleeps.
/// </remarks>
public sealed class FileWatcherServiceDirectoryChangeTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    private readonly string _root = Path.Join(Path.GetTempPath(), $"fws_dirs_{Guid.NewGuid():N}");
    private readonly string _library;
    private readonly string _staging;
    private readonly Mock<IFileStoreService> _store = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly ConcurrentQueue<string> _logs = new();
    private readonly ConcurrentQueue<string> _processed = new();
    private readonly List<FileWatcherService> _services = new();

    public FileWatcherServiceDirectoryChangeTests()
    {
        _library = Path.Join(_root, "library");
        _staging = Path.Join(_root, "staging");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_staging);

        _store.Setup(s => s.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ComicFile>());
        _processor.Setup(p => p.ProcessFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((path, _) =>
            {
                _processed.Enqueue(path);
                return Task.FromResult(true);
            });
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        TestIO.TryDeleteDirectory(_root);
    }

    [Fact]
    public async Task DirectoryRenamed_MovesItsTrackedFilesInTheStore()
    {
        var oldDir = CreateSeries("One Piece", "One Piece - Chapter 0001.cbz");
        var newDir = Path.Join(_library, "One Piece (2023)");
        _store.Setup(s => s.FileExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _store.Setup(s => s.MoveDirectoryAsync(oldDir, newDir, It.IsAny<CancellationToken>())).ReturnsAsync(1);
        await StartWatchingAsync();

        Directory.Move(oldDir, newDir);

        await WaitForLogAsync($"Directory renamed: {oldDir} -> {newDir}; moved 1 tracked file(s)");
        _store.Verify(s => s.MoveDirectoryAsync(oldDir, newDir, It.IsAny<CancellationToken>()), Times.Once);
        // The moved file is tracked under its new path already; it is not added again or removed.
        _store.Verify(s => s.AddFileAsync(It.Is<string>(p => p.StartsWith(newDir, StringComparison.Ordinal)), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DirectoryRenamed_TracksAndQueuesTheComicsUnderItTheStoreDidNotTrack()
    {
        var oldDir = CreateSeries("Saga", "Saga 001.cbz");
        var newDir = Path.Join(_library, "Saga (2012)");
        await StartWatchingAsync();

        Directory.Move(oldDir, newDir);

        var moved = Path.Join(newDir, "Saga 001.cbz");
        await WaitUntilAsync(() => _processed.Contains(moved), $"{moved} to be processed");
        _store.Verify(s => s.MoveDirectoryAsync(oldDir, newDir, It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.AddFileAsync(moved, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DirectoryRenamed_FilesWaitingToSettleUnderIt_AreProcessedAtTheirNewPath()
    {
        var oldDir = CreateSeries("Berserk");
        var newDir = Path.Join(_library, "Berserk (1989)");
        _store.Setup(s => s.FileExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await StartWatchingAsync(stabilitySeconds: 2);
        var arrived = MoveIn("Berserk 001.cbz", oldDir);
        await WaitForLogAsync($"File created: {arrived}");

        Directory.Move(oldDir, newDir);

        var moved = Path.Join(newDir, "Berserk 001.cbz");
        await WaitUntilAsync(() => _processed.Contains(moved), $"{moved} to be processed");
        Assert.DoesNotContain(arrived, _processed);
    }

    [Fact]
    public async Task DirectoryMovedOutOfTheLibrary_RemovesItsTrackedFilesFromTheStore()
    {
        var series = CreateSeries("Monster", "Monster 001.cbz", "Monster 002.cbz");
        await StartWatchingAsync();

        Directory.Move(series, Path.Join(_staging, "Monster"));

        await WaitUntilAsync(
            () => _store.Invocations.Any(i => i.Method.Name == nameof(IFileStoreService.RemoveDirectoryAsync) && Equals(i.Arguments[0], series)),
            $"the files under {series} to be removed");
        _store.Verify(s => s.MoveDirectoryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DirectoryDeleted_RemovesItsTrackedFilesFromTheStore()
    {
        var series = CreateSeries("Vagabond", "Vagabond 001.cbz");
        Directory.CreateDirectory(Path.Join(series, "Extras"));
        await StartWatchingAsync();

        Directory.Delete(series, recursive: true);

        await WaitUntilAsync(
            () => _store.Invocations.Any(i => i.Method.Name == nameof(IFileStoreService.RemoveDirectoryAsync) && Equals(i.Arguments[0], series)),
            $"the files under {series} to be removed");
    }

    [Fact]
    public async Task ComicRenamedToANonComicName_IsRemovedFromTheStore()
    {
        var series = CreateSeries("Akira", "Akira 001.cbz");
        var comic = Path.Join(series, "Akira 001.cbz");
        await StartWatchingAsync();

        File.Move(comic, comic + ".bak");

        await WaitUntilAsync(
            () => _store.Invocations.Any(i => i.Method.Name == nameof(IFileStoreService.RemoveFileAsync) && Equals(i.Arguments[0], comic)),
            $"{comic} to be removed");
        await WaitForLogAsync($"File renamed to a non-comic name: {comic} -> {comic}.bak");
        _store.Verify(s => s.UpdateFilePathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// The processor rewrites an archive with File.Replace(temp, archive, archive + ".backup"),
    /// which on Windows is reported as the archive being renamed to its backup name and a new
    /// file appearing at its path. The archive never left the library: its row, with its
    /// processing state, user metadata edits and field locks, must not be removed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArchiveRewrittenWithFileReplace_IsNotRemovedFromTheStore(bool tempFileInTheLibrary)
    {
        var series = CreateSeries("Akira", "Akira 001.cbz", "Akira 002.cbz");
        var comic = Path.Join(series, "Akira 001.cbz");
        var renamedAway = Path.Join(series, "Akira 002.cbz");
        await StartWatchingAsync();

        // What ComicProcessorService.ReplaceFileWithRetryAsync does.
        var temp = Path.Join(tempFileInTheLibrary ? series : _staging, $".tmp_{Guid.NewGuid()}.cbz");
        File.WriteAllText(temp, "rewritten");
        File.Replace(temp, comic, comic + ".backup");
        File.Delete(comic + ".backup");

        // A comic really renamed away is removed through the same path, after the rewrite's
        // events: once it is, they have all been handled.
        File.Move(renamedAway, renamedAway + ".bak");
        await WaitUntilAsync(() => RemoveFileCalledFor(renamedAway), $"{renamedAway} to be removed");

        Assert.False(RemoveFileCalledFor(comic), $"{comic} was removed from the store although it was only rewritten");
        Assert.Equal("rewritten", File.ReadAllText(comic));
    }

    [Fact]
    public async Task ComicRenamedAsideAndReplacedByAnotherFile_IsNotRemovedFromTheStore()
    {
        var series = CreateSeries("Akira", "Akira 001.cbz");
        var comic = Path.Join(series, "Akira 001.cbz");
        await StartWatchingAsync(stabilitySeconds: 1);

        // A tool's safe save: keep the old file aside, then move the new one into place.
        File.Move(comic, comic + ".bak");
        MoveIn("Akira 001.cbz", series);

        await WaitForLogAsync($"File was replaced in place rather than renamed away, keeping it: {comic}");
        Assert.False(RemoveFileCalledFor(comic), $"{comic} was removed from the store although it was replaced in place");
    }

    private bool RemoveFileCalledFor(string path) =>
        _store.Invocations.Any(i => i.Method.Name == nameof(IFileStoreService.RemoveFileAsync) && Equals(i.Arguments[0], path));

    private string CreateSeries(string name, params string[] fileNames)
    {
        var directory = Path.Join(_library, name);
        Directory.CreateDirectory(directory);
        foreach (var fileName in fileNames)
        {
            File.WriteAllText(Path.Join(directory, fileName), "comic");
        }

        return directory;
    }

    /// <summary>
    /// Moves a new file in from the unwatched staging directory, so its arrival raises a single
    /// "created" notification.
    /// </summary>
    private string MoveIn(string fileName, string directory)
    {
        var staged = Path.Join(_staging, fileName);
        File.WriteAllText(staged, "comic");
        var target = Path.Join(directory, fileName);
        File.Move(staged, target);
        return target;
    }

    /// <summary>
    /// Starts a watcher and waits for its one-off initial scan so the scan cannot interleave with
    /// the events a test produces.
    /// </summary>
    private async Task StartWatchingAsync(int stabilitySeconds = 0)
    {
        var settings = new AppSettings
        {
            WatchedDirectory = _library,
            WatcherEnableRename = true,
            WatcherEnableNormalize = true,
            WatcherFileStabilityDelaySeconds = stabilitySeconds,
            WatcherDirectoryScanDelaySeconds = 0,
        };
        var service = TestServices.CreateFileWatcher(
            new TestOptionsMonitor<AppSettings>(settings),
            new QueueLogger(_logs),
            _store.Object,
            _processor.Object);
        _services.Add(service);

        await service.StartAsync();
        Assert.True(service.IsRunning);
        await WaitForLogAsync("Incremental scan completed");
    }

    private Task WaitForLogAsync(string fragment) =>
        WaitUntilAsync(
            () => _logs.Any(message => message.Contains(fragment, StringComparison.Ordinal)),
            $"a log containing \"{fragment}\"",
            () => $". Logged:{Environment.NewLine}{string.Join(Environment.NewLine, _logs)}");

    private static async Task WaitUntilAsync(Func<bool> condition, string description, Func<string>? details = null)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out after {WaitTimeout.TotalSeconds:0}s waiting for {description}{details?.Invoke()}");
            }

            await Task.Delay(20);
        }
    }

    private sealed class QueueLogger(ConcurrentQueue<string> messages) : ILogger<FileWatcherService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Enqueue(formatter(state, exception));
    }
}
