using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers <see cref="FolderCombineService"/> directly: what a combine does to
/// the source folders once their comics have moved, what the preview says it
/// will do, the shared library mutation lock, and where added dates come from.
/// Discovery and the endpoints are covered end to end by
/// FilesControllerFolderCombineTests.
/// </summary>
public sealed class FolderCombineServiceTests : IDisposable
{
    private static readonly DateTime T1 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Join(Path.GetTempPath(), $"fc-service-{Guid.NewGuid():N}");
    private readonly string _library;
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<IProcessingHistoryService> _history = new();
    private readonly Mock<ISeriesLibraryService> _seriesLibrary = new();
    private readonly Mock<IOptionsMonitor<AppSettings>> _settings = new();
    private readonly List<ProcessingHistoryEntry> _historyEntries = new();
    private readonly RecordingBatchJobReport _jobReport;
    private List<ComicFile> _files = new();

    public FolderCombineServiceTests()
    {
        _library = Directory.CreateDirectory(Path.Join(_root, "library")).FullName;
        _settings.Setup(s => s.CurrentValue).Returns(new AppSettings
        {
            WatchedDirectory = _library,
            DuplicateDirectory = Path.Join(_root, "duplicates")
        });
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _files);
        _history
            .Setup(h => h.AddHistoryEntryAsync(It.IsAny<ProcessingHistoryEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessingHistoryEntry, CancellationToken>((entry, _) =>
            {
                lock (_historyEntries)
                {
                    _historyEntries.Add(entry);
                }
            })
            .Returns(Task.CompletedTask);
        _processor
            .Setup(p => p.NormalizeAndRenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        _jobReport = _processor.RunCustomBatchJobsInline();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private FolderCombineService CreateService(ILibraryMutationLock? libraryMutationLock = null) =>
        TestFolderCombineService.Create(
            _fileStore.Object,
            _processor.Object,
            _history.Object,
            _seriesLibrary.Object,
            _settings.Object,
            libraryMutationLock: libraryMutationLock);

    private string Dir(params string[] parts) => Path.Join(new[] { _library }.Concat(parts).ToArray());

    /// <summary>Writes a file on disk and returns its path.</summary>
    private static string Write(string directory, string name, string content = "x")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Writes a comic on disk and tracks it in the file store as part of series "Berserk".</summary>
    private ComicFile Track(string directory, string name, DateTime modified, DateTime createdAt = default)
    {
        var file = new ComicFile
        {
            FilePath = Write(directory, name),
            FileName = name,
            Directory = directory,
            Metadata = new ComicMetadata { Series = "Berserk" },
            LastModified = modified,
            CreatedAt = createdAt,
            FileSize = 1
        };
        _files.Add(file);
        return file;
    }

    private static FolderCombineRequest Combine(string destination, params string[] sources) => new()
    {
        GroupKey = "berserk",
        DestinationDirectory = destination,
        SourceDirectories = sources.ToList()
    };

    private List<ProcessingHistoryEntry> HistoryFor(string path)
    {
        lock (_historyEntries)
        {
            return _historyEntries.Where(e => e.FilePath == path).ToList();
        }
    }

    // ------------------------------------------------------------ the finding

    [Fact]
    public async Task StartAsync_SourceWithFilesTheAppDoesNotOwn_MovesTheComicsKeepsTheFolderAndReportsWhatIsLeft()
    {
        // FP-8: combining "Berserk (Digital)" into "Berserk" used to delete the
        // PDF, the artbook folder and everything else left in the source.
        var destination = Dir("Berserk");
        var source = Dir("Berserk (Digital)");
        Track(destination, "Berserk 001.cbz", T1);
        var moved = Track(source, "Berserk 002.cbz", T2);
        var comicInfo = Write(source, "ComicInfo.xml");
        var pdf = Write(source, "Berserk v01.pdf");
        var art = Write(Path.Join(source, "Extras"), "art1.jpg");

        var started = await CreateService().StartAsync(Combine(destination, source));

        Assert.Null(started.Error);
        Assert.NotNull(started.JobId);
        Assert.True(File.Exists(Path.Join(destination, "Berserk 002.cbz")));
        Assert.False(File.Exists(moved.FilePath));
        // Nothing the app does not own is deleted, and a kept folder keeps
        // even its disposable files.
        Assert.All(new[] { comicInfo, pdf, art }, path => Assert.True(File.Exists(path)));
        var problem = Assert.Single(_jobReport.Problems);
        Assert.Equal(source, problem.Key);
        Assert.StartsWith("Source folder kept. ", problem.Value);
        Assert.Contains("Berserk v01.pdf", problem.Value);
        Assert.Contains(Path.Join("Extras", "art1.jpg"), problem.Value);
        var history = Assert.Single(HistoryFor(source));
        Assert.Equal(FolderCombineService.HistoryAction, history.Action);
        Assert.False(history.Success);
        Assert.Equal(problem.Value, history.ErrorMessage);
    }

    [Fact]
    public async Task StartAsync_SourceLeftWithOnlyDisposableFiles_DeletesThemAndTheFolder()
    {
        var destination = Dir("Berserk");
        var source = Dir("Berserk (Digital)");
        Track(destination, "Berserk 001.cbz", T1);
        Track(source, "Berserk 002.cbz", T2);
        Write(source, "ComicInfo.xml");
        Write(source, "cover.jpg");
        Directory.CreateDirectory(Path.Join(source, "Scans", "Empty"));

        await CreateService().StartAsync(Combine(destination, source));

        Assert.False(Directory.Exists(source));
        Assert.Empty(_jobReport.Problems);
        var history = Assert.Single(HistoryFor(source));
        Assert.True(history.Success);
        Assert.Equal("Removed the emptied source folder; deleted 2 disposable file(s): ComicInfo.xml, cover.jpg", history.ErrorMessage);
    }

    [Fact]
    public async Task StartAsync_AMoveFails_KeepsTheSourceFolderWithTheComicThatStayed()
    {
        var destination = Dir("Berserk");
        var source = Dir("Berserk (Digital)");
        Track(destination, "Berserk 001.cbz", T1);
        var stays = Track(source, "Berserk 002.cbz", T2);
        Write(source, "ComicInfo.xml");
        var squatter = Path.Join(destination, "Berserk 002.cbz");
        var moveResults = new List<bool>();
        _processor
            .Setup(p => p.RunCustomBatchJobAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<Func<string, CancellationToken, Task<bool>>>(),
                It.IsAny<string>(),
                It.IsAny<Func<IBatchJobReport, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string _, IEnumerable<string> items, Func<string, CancellationToken, Task<bool>> operation, string _, Func<IBatchJobReport, CancellationToken, Task> postLoop, CancellationToken token) =>
            {
                // A file appears at the planned destination after planning, so
                // the move (which never overwrites) fails.
                File.WriteAllText(squatter, "squatter");
                foreach (var item in items)
                {
                    moveResults.Add(await operation(item, token));
                }
                await postLoop(_jobReport, token);
                return Guid.NewGuid();
            });

        await CreateService().StartAsync(Combine(destination, source));

        Assert.Equal(new[] { false }, moveResults);
        Assert.True(File.Exists(stays.FilePath));
        Assert.Equal("squatter", File.ReadAllText(squatter));
        Assert.False(Assert.Single(HistoryFor(stays.FilePath)).Success);
        var problem = Assert.Single(_jobReport.Problems);
        Assert.Equal(source, problem.Key);
        Assert.Contains("Berserk 002.cbz", problem.Value);
        _fileStore.Verify(f => f.UpdateFilePathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_NestedSourceFolders_RemovesTheInnerOneFirstSoTheOuterOneCanGoToo()
    {
        var destination = Dir("Berserk");
        var outer = Dir("Berserk Old");
        var inner = Dir("Berserk Old", "Part 2");
        Track(destination, "Berserk 001.cbz", T1);
        Track(outer, "Berserk 002.cbz", T2);
        Track(inner, "Berserk 003.cbz", T2);
        Write(outer, "ComicInfo.xml");
        Write(inner, "ComicInfo.xml");

        var service = CreateService();
        var preview = await service.PreviewAsync(Combine(destination, outer, inner));
        await service.StartAsync(Combine(destination, outer, inner));

        Assert.All(preview.Plan!.SourceCleanup, cleanup => Assert.True(cleanup.Removable));
        Assert.False(Directory.Exists(inner));
        Assert.False(Directory.Exists(outer));
        Assert.Empty(_jobReport.Problems);
    }

    [Fact]
    public async Task StartAsync_LooseFilesInTheWatchedFolder_NeverRemovesTheWatchedFolder()
    {
        var destination = Dir("Berserk");
        Track(destination, "Berserk 001.cbz", T1);
        Track(_library, "Berserk 002.cbz", T2);

        await CreateService().StartAsync(Combine(destination, _library));

        Assert.True(Directory.Exists(_library));
        Assert.True(File.Exists(Path.Join(destination, "Berserk 002.cbz")));
        var problem = Assert.Single(_jobReport.Problems);
        Assert.Equal(_library, problem.Key);
        Assert.Contains(FolderCombineSourceCleaner.ProtectedFolderReason, problem.Value);
    }

    // --------------------------------------------------------------- preview

    [Fact]
    public async Task PreviewAsync_ListsWhichSourceFoldersWillBeRemovedAndWhatKeepsTheOthers()
    {
        var destination = Dir("Berserk");
        var clean = Dir("Berserk (Digital)");
        var cluttered = Dir("Berserk (Scans)");
        Track(destination, "Berserk 001.cbz", T1);
        var cleanComic = Track(clean, "Berserk 002.cbz", T2);
        Track(cluttered, "Berserk 003.cbz", T2);
        Write(clean, "ComicInfo.xml");
        var pdf = Write(cluttered, "Berserk v01.pdf");

        var result = await CreateService().PreviewAsync(Combine(destination, clean, cluttered));

        Assert.Null(result.Error);
        var cleanup = result.Plan!.SourceCleanup;
        Assert.Equal(new[] { clean, cluttered }, cleanup.Select(c => c.Directory));
        Assert.True(cleanup[0].Removable);
        Assert.Equal(new[] { "ComicInfo.xml" }, cleanup[0].DisposableFiles);
        Assert.False(cleanup[1].Removable);
        Assert.Equal(new[] { "Berserk v01.pdf" }, cleanup[1].Leftovers);
        Assert.Equal(1, cleanup[1].LeftoverCount);
        // A preview changes nothing.
        Assert.True(File.Exists(cleanComic.FilePath));
        Assert.True(File.Exists(pdf));
        Assert.True(File.Exists(Path.Join(clean, "ComicInfo.xml")));
        _processor.Verify(p => p.RunCustomBatchJobAsync(
            It.IsAny<string>(),
            It.IsAny<IEnumerable<string>>(),
            It.IsAny<Func<string, CancellationToken, Task<bool>>>(),
            It.IsAny<string>(),
            It.IsAny<Func<IBatchJobReport, CancellationToken, Task>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ------------------------------------------------------------------ lock

    /// <summary>Notes, as each locked section starts and ends, whether a folder exists.</summary>
    private sealed class RecordingLock(string watchedFolder) : ILibraryMutationLock
    {
        private readonly LibraryMutationLock _inner = new();

        public List<(bool AtStart, bool AtEnd)> Sections { get; } = new();

        public T Execute<T>(Func<T> action, CancellationToken cancellationToken = default) =>
            _inner.Execute(action, cancellationToken);

        public void Execute(Action action, CancellationToken cancellationToken = default) =>
            _inner.Execute(action, cancellationToken);

        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default) =>
            _inner.ExecuteAsync(async token =>
            {
                var atStart = Directory.Exists(watchedFolder);
                var result = await action(token);
                Sections.Add((atStart, Directory.Exists(watchedFolder)));
                return result;
            }, cancellationToken);
    }

    [Fact]
    public async Task StartAsync_RemovesEachSourceFolderInsideALockedSection()
    {
        var destination = Dir("Berserk");
        var source = Dir("Berserk (Digital)");
        Track(destination, "Berserk 001.cbz", T1);
        Track(source, "Berserk 002.cbz", T2);
        var recording = new RecordingLock(source);

        await CreateService(recording).StartAsync(Combine(destination, source));

        Assert.Equal(new[] { (true, false) }, recording.Sections);
    }

    [Fact]
    public async Task StartAsync_WhileAnImportHoldsTheLock_WaitsAndThenKeepsTheFolderTheImportStagedInto()
    {
        var destination = Dir("Berserk");
        var source = Dir("Berserk (Digital)");
        Track(destination, "Berserk 001.cbz", T1);
        Track(source, "Berserk 002.cbz", T2);
        using var sharedLock = new LibraryMutationLock();
        var importerMayStage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staged = Path.Join(source, ".tmp_0123456789abcdef.suwayomi-import");
        // Stands in for the Suwayomi importer: it holds the lock while it
        // stages a chapter into the source folder.
        var importer = sharedLock.ExecuteAsync(async _ =>
        {
            await importerMayStage.Task;
            File.WriteAllText(staged, "chapter");
            return true;
        });

        // The combine moves the comic, then waits for the lock before it looks
        // at what is left in the source folder.
        var combine = CreateService(sharedLock).StartAsync(Combine(destination, source));
        Assert.False(combine.IsCompleted);
        Assert.True(Directory.Exists(source));

        importerMayStage.TrySetResult();
        await importer.WaitAsync(Timeout);
        await combine.WaitAsync(Timeout);

        Assert.True(File.Exists(staged));
        var problem = Assert.Single(_jobReport.Problems);
        Assert.Contains(Path.GetFileName(staged), problem.Value);
    }

    // ------------------------------------------------------------ added dates

    [Fact]
    public async Task GetCombinableFolderGroupsAsync_SuggestsTheFolderWithTheNewestAddedDateFromTheFileStore()
    {
        // "Old Scans" holds the most recently modified file, but "New Release"
        // holds the file the library added last, and that one wins.
        var newRelease = Dir("New Release");
        var oldScans = Dir("Old Scans");
        Track(newRelease, "Berserk 001.cbz", modified: T1, createdAt: T2);
        Track(oldScans, "Berserk 002.cbz", modified: T2, createdAt: T1);

        var group = Assert.Single(await CreateService().GetCombinableFolderGroupsAsync());

        Assert.Equal(newRelease, group.SuggestedDestinationDirectory);
        Assert.Equal(T2, group.Folders.Single(f => f.Directory == newRelease).NewestFileAddedAt);
        Assert.Equal(T1, group.Folders.Single(f => f.Directory == oldScans).NewestFileAddedAt);
    }

    [Fact]
    public async Task GetCombinableFolderGroupsAsync_FileWithoutAnAddedDate_UsesItsModificationTime()
    {
        var a = Dir("A");
        var b = Dir("B");
        Track(a, "Berserk 001.cbz", modified: T1);
        Track(b, "Berserk 002.cbz", modified: T2);

        var service = CreateService();
        var group = Assert.Single(await service.GetCombinableFolderGroupsAsync());

        Assert.Equal(b, group.SuggestedDestinationDirectory);
        Assert.Equal(T2, group.Folders.Single(f => f.Directory == b).NewestFileAddedAt);
        Assert.Equal(1, await service.GetCombinableFolderCountAsync());
    }
}
