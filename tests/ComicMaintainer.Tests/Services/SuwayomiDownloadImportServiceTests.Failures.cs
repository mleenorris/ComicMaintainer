using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// The folder settings the import refuses, and what a run leaves behind when something fails part way: a chapter is
/// never lost, and everything that was not finished is found by the next run.
/// </summary>
public sealed partial class SuwayomiDownloadImportServiceTests
{
    private const string StagingName = ".tmp_0123456789abcdef.suwayomi-import";

    // ----- folder settings ------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolveDownloadsDirectory_RequiresAFolder(string? configured)
    {
        Assert.False(SuwayomiDownloadImportService.TryResolveDownloadsDirectory(configured, _settings, out _, out var error));

        Assert.Equal("No Suwayomi downloads folder is configured.", error);
    }

    [Fact]
    public void TryResolveDownloadsDirectory_RefusesAPathThatCannotBeResolved()
    {
        var unusable = Path.Join(_root, "bad\0name");

        Assert.False(SuwayomiDownloadImportService.TryResolveDownloadsDirectory(unusable, _settings, out _, out var error));

        Assert.Equal($"The Suwayomi downloads folder '{unusable}' is not a valid path.", error);
    }

    [Fact]
    public void TryResolveDownloadsDirectory_RefusesTheRootOfADrive()
    {
        Assert.False(SuwayomiDownloadImportService.TryResolveDownloadsDirectory(Path.GetPathRoot(_root), _settings, out _, out var error));

        Assert.Equal("The Suwayomi downloads folder cannot be the root of a drive.", error);
    }

    [Fact]
    public void TryResolveDownloadsDirectory_IgnoresProtectedFoldersThatAreNotSet()
    {
        _settings.DuplicateDirectory = "   ";

        Assert.True(SuwayomiDownloadImportService.TryResolveDownloadsDirectory(_downloads, _settings, out var resolved, out var error));

        Assert.Null(error);
        Assert.Equal(_downloads, resolved);
    }

    // A protected folder that is a volume root contains everything on that volume, however it is spelled.
    [Fact]
    public void TryResolveDownloadsDirectory_RefusesAFolderOnAVolumeThatIsItselfProtected()
    {
        _settings.TempFileDirectory = Path.GetPathRoot(_root)!;

        Assert.False(SuwayomiDownloadImportService.TryResolveDownloadsDirectory(_downloads, _settings, out _, out var error));

        Assert.Contains("temp directory", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolveImportDirectory_RequiresAWatchedDirectory()
    {
        _settings.WatchedDirectory = "  ";

        Assert.False(SuwayomiDownloadImportService.TryResolveImportDirectory("Imports", _settings, out _, out var error));

        Assert.Equal("No watched directory is configured.", error);
    }

    [Fact]
    public void TryResolveImportDirectory_RefusesAFolderInsideAProtectedDirectory()
    {
        _settings.DuplicateDirectory = Path.Join(_library, "Duplicates");

        Assert.False(SuwayomiDownloadImportService.TryResolveImportDirectory("Duplicates/New", _settings, out _, out var error));

        Assert.Contains("must not be inside the duplicates directory", error, StringComparison.Ordinal);
    }

    [Fact]
    public void IsCompleteArchive_IsFalseForAFolder()
    {
        Assert.False(SuwayomiDownloadImportService.IsCompleteArchive(_downloads));
    }

    // ----- runs that do not go to plan --------------------------------------

    [Fact]
    public async Task ImportAsync_WhileAnotherImportIsRunning_SkipsInsteadOfWaitingForIt()
    {
        Download("Source", "Solo Leveling", "Chapter 1.cbz");
        using var blockingLock = new BlockingLibraryMutationLock();
        _libraryMutationLock = blockingLock;
        var service = CreateService();
        var firstRun = Task.Run(() => service.ImportAsync());
        Assert.True(blockingLock.Entered.Wait(TimeSpan.FromSeconds(30)), "The first import never reached the library lock");

        var second = await service.ImportAsync();

        Assert.Equal("Another Suwayomi download import is already running.", second.SkippedReason);
        blockingLock.Release.Set();
        Assert.Single((await firstRun).Imported);
    }

    // A series folder named only with punctuation has no key to match a library series by.
    [Fact]
    public async Task ImportAsync_ASeriesFolderOfOnlyPunctuation_StillGetsAFolderOfItsOwn()
    {
        var source = Download("Source", "!!!", "Chapter 1.cbz");

        var result = await CreateService().ImportAsync();

        var imported = Assert.Single(result.Imported);
        Assert.Equal(Path.Join(_library, "!!!", "Chapter 1.cbz"), imported.DestinationPath);
        Assert.True(File.Exists(imported.DestinationPath));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task ImportAsync_RunsNormallyWithAConfigFolderButNoJournalYet()
    {
        Directory.CreateDirectory(_settings.ConfigDirectory);
        Download("Source", "Solo Leveling", "Chapter 1.cbz");

        var result = await CreateService().ImportAsync();

        Assert.Single(result.Imported);
    }

    [Fact]
    public async Task ImportAsync_NeverImportsIntoAProtectedFolderInsideTheLibrary()
    {
        _settings.DuplicateDirectory = Directory.CreateDirectory(Path.Join(_library, "Duplicates")).FullName;
        var chapter1 = Download("Source", "Duplicates", "Chapter 1.cbz");
        var chapter2 = Download("Source", "Duplicates", "Chapter 2.cbz");

        var result = await CreateService().ImportAsync();

        Assert.Empty(result.Imported);
        Assert.Equal(2, result.Failed);
        Assert.True(File.Exists(chapter1));
        Assert.True(File.Exists(chapter2));
        Assert.Empty(Directory.GetFiles(_settings.DuplicateDirectory));
    }

    // Common after a user renames a chapter file to the series's name: the library folder cannot be created.
    [Fact]
    public async Task ImportAsync_WhenTheSeriesFolderCannotBeCreated_LeavesTheDownloadInPlace()
    {
        var source = Download("Source", "Solo Leveling", "Chapter 1.cbz", "original chapter");
        var fileInTheWay = Path.Join(_library, "Solo Leveling");
        File.WriteAllText(fileInTheWay, "a file, not a folder");

        var result = await CreateService().ImportAsync();

        Assert.Empty(result.Imported);
        Assert.Equal(1, result.Failed);
        Assert.Equal("original chapter", ReadPage(source));
        Assert.Equal("a file, not a folder", File.ReadAllText(fileInTheWay));
        Assert.False(File.Exists(PendingMovesPath));
    }

    [Fact]
    public async Task ImportAsync_WhenThePendingJournalCannotBeWritten_LeavesTheDownloadUnmoved()
    {
        var source = Download("Source", "Solo Leveling", "Chapter 1.cbz", "original chapter");
        var destinationFolder = Path.Join(_library, "Solo Leveling");
        Directory.CreateDirectory(PendingMovesPath + ".tmp");

        var result = await CreateService().ImportAsync();

        Assert.Empty(result.Imported);
        Assert.True(result.Failed >= 1, "The chapter could not be journaled, so nothing may have moved");
        Assert.Equal("original chapter", ReadPage(source));
        Assert.Empty(Directory.GetFiles(destinationFolder, "*" + ".suwayomi-import"));
    }

    // The moved-away source of a chapter whose rename fails under the lock is reported as gone, not as a failure.
    [Fact]
    public async Task ImportAsync_WhenTheDownloadDisappearsBeforeItIsStaged_ReportsNothingAndKeepsTheFolderClean()
    {
        var source = Download("Source", "Solo Leveling", "Chapter 1.cbz");
        Download("Source", "Solo Leveling", "Chapter 2.cbz");
        _libraryMutationLock = new SourceDeletingLibraryMutationLock(source);

        var result = await CreateService().ImportAsync();

        var imported = Assert.Single(result.Imported);
        Assert.Equal("Chapter 2.cbz", Path.GetFileName(imported.DestinationPath));
        Assert.Equal(0, result.Failed);
        Assert.Empty(Directory.GetFiles(Path.Join(_library, "Solo Leveling"), "*.suwayomi-import"));
    }

    [Fact]
    public async Task ImportAsync_ForgetsAJournalEntryWhoseStagedFileIsAlreadyGone()
    {
        var source = Path.Join(_downloads, "Source", "Solo Leveling", "Chapter 1.cbz");
        var staging = Path.Join(_library, "Solo Leveling", StagingName);
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        WritePendingMove(staging, source);

        var result = await CreateService().ImportAsync();

        Assert.Empty(result.Imported);
        Assert.Equal(0, result.Failed);
        Assert.False(File.Exists(PendingMovesPath));
    }

    [Fact]
    public async Task ImportAsync_DropsAJournalEntryThatPointsIntoAProtectedFolder()
    {
        _settings.DuplicateDirectory = Directory.CreateDirectory(Path.Join(_library, "Duplicates")).FullName;
        var source = Path.Join(_downloads, "Source", "Solo Leveling", "Chapter 1.cbz");
        var staging = WriteCbz(Path.Join(_settings.DuplicateDirectory, StagingName), "staged copy");
        WritePendingMove(staging, source);

        var result = await CreateService().ImportAsync();

        Assert.Empty(result.Imported);
        Assert.Equal("staged copy", ReadPage(staging));
        Assert.False(File.Exists(PendingMovesPath));
    }

    [Fact]
    public async Task ImportAsync_AStagedChapterDamagedDuringRollback_StaysStagedAndKeepsItsJournalEntry()
    {
        var source = Download("Source", "Solo Leveling", "Chapter 1.cbz", "original chapter");
        var destinationFolder = OccupyEveryDestinationName(Path.GetFileName(source));
        var logger = new HookLogger("returning it to the downloads folder", () =>
        {
            var staged = Assert.Single(Directory.GetFiles(destinationFolder, "*.suwayomi-import"));
            File.WriteAllText(staged, "not an archive any more");
        });

        var result = await CreateService(logger).ImportAsync();

        Assert.Equal(1, result.Failed);
        Assert.False(File.Exists(source), "A damaged staged file must not be handed back as the download");
        var staging = Assert.Single(Directory.GetFiles(destinationFolder, "*.suwayomi-import"));
        Assert.Contains(staging, JournaledStagingPaths());
        Assert.Empty(Directory.GetFiles(_downloads, ".tmp_*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ImportAsync_WhenTheDownloadFolderCannotBeRestored_LeavesTheChapterStagedForTheNextRun()
    {
        var source = Download("Source", "Solo Leveling", "Chapter 1.cbz", "original chapter");
        var seriesFolder = Path.GetDirectoryName(source)!;
        var destinationFolder = OccupyEveryDestinationName(Path.GetFileName(source));
        var logger = new HookLogger("returning it to the downloads folder", () =>
        {
            Directory.Delete(seriesFolder, recursive: true);
            File.WriteAllText(seriesFolder, "a file, not a folder");
        });

        var result = await CreateService(logger).ImportAsync();

        Assert.Equal(1, result.Failed);
        var staging = Assert.Single(Directory.GetFiles(destinationFolder, "*.suwayomi-import"));
        Assert.Equal("original chapter", ReadPage(staging));
        Assert.Contains(staging, JournaledStagingPaths());
    }

    private string OccupyEveryDestinationName(string fileName)
    {
        var destinationFolder = Directory.CreateDirectory(Path.Join(_library, "Solo Leveling")).FullName;
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        foreach (var attempt in Enumerable.Range(1, 1000))
        {
            var name = attempt == 1 ? fileName : $"{baseName} ({attempt}){extension}";
            File.WriteAllText(Path.Join(destinationFolder, name), $"existing file {attempt}");
        }

        return destinationFolder;
    }

    /// <summary>Holds every locked section until released, as a slow file operation would.</summary>
    private sealed class BlockingLibraryMutationLock : ILibraryMutationLock, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public T Execute<T>(Func<T> action, CancellationToken cancellationToken = default)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(60), cancellationToken);
            return action();
        }

        public void Execute(Action action, CancellationToken cancellationToken = default) =>
            Execute(() => { action(); return true; }, cancellationToken);

        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default) =>
            action(cancellationToken);

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }

    /// <summary>Removes one chapter just before the locked section that moves it, like a concurrent delete.</summary>
    private sealed class SourceDeletingLibraryMutationLock(string source) : ILibraryMutationLock
    {
        private readonly LibraryMutationLock _inner = new();

        private int _sections;

        public T Execute<T>(Func<T> action, CancellationToken cancellationToken = default)
        {
            // The first section recovers pending moves; the second moves the first chapter.
            if (Interlocked.Increment(ref _sections) == 2 && File.Exists(source))
            {
                File.Delete(source);
            }

            return _inner.Execute(action, cancellationToken);
        }

        public void Execute(Action action, CancellationToken cancellationToken = default) =>
            Execute(() => { action(); return true; }, cancellationToken);

        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default) =>
            _inner.ExecuteAsync(action, cancellationToken);
    }
}
