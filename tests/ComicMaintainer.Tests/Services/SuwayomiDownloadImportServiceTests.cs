using System.IO.Compression;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace ComicMaintainer.Tests.Services;

public sealed class SuwayomiDownloadImportServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _library;
    private readonly string _downloads;
    private readonly AppSettings _settings;
    private readonly Mock<ISeriesLibraryService> _seriesLibrary = new();

    // An hour ahead, so everything a test writes counts as finished.
    private FakeTimeProvider _time = new(DateTimeOffset.UtcNow.AddHours(1));

    public SuwayomiDownloadImportServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"SuwayomiImportTests_{Guid.NewGuid():N}");
        _library = Directory.CreateDirectory(Path.Combine(_root, "library")).FullName;
        _downloads = Directory.CreateDirectory(Path.Combine(_root, "suwayomi", "mangas")).FullName;

        _settings = new AppSettings
        {
            WatchedDirectory = _library,
            DuplicateDirectory = Path.Combine(_root, "duplicates"),
            ConfigDirectory = Path.Combine(_root, "config"),
            TempFileDirectory = Path.Combine(_root, "config", "temp"),
            SuwayomiDownloadsDirectory = _downloads
        };

        SetupLibrary(Array.Empty<SeriesSummaryDto>(), Array.Empty<SeriesFoldersResult>());
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

    private SuwayomiDownloadImportService CreateService()
    {
        var monitor = new Mock<IOptionsMonitor<AppSettings>>();
        monitor.Setup(m => m.CurrentValue).Returns(_settings);
        return new SuwayomiDownloadImportService(
            monitor.Object,
            _seriesLibrary.Object,
            NullLogger<SuwayomiDownloadImportService>.Instance,
            _time);
    }

    private void SetupLibrary(IEnumerable<SeriesSummaryDto> series, IEnumerable<SeriesFoldersResult> folders)
    {
        _seriesLibrary
            .Setup(l => l.GetSeriesSummariesAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), 1, -1, It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesSummaryResult { Series = series.ToList() });
        _seriesLibrary
            .Setup(l => l.GetAllSeriesFolderGroupsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(folders.ToList());
    }

    private static SeriesFoldersResult Folders(string id, params string[] directories) => new()
    {
        Id = id,
        Folders = directories.Select((directory, index) => new SeriesFolderDto
        {
            Directory = directory,
            FileCount = directories.Length - index
        }).ToList()
    };

    private static string WriteCbz(string path, string page = "page")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("001.jpg").Open());
            writer.Write(page);
        }

        return path;
    }

    private static string ReadPage(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("001.jpg")!.Open());
        return reader.ReadToEnd();
    }

    private string Download(string source, string series, string chapter)
        => WriteCbz(Path.Combine(_downloads, source, series, chapter));

    [Fact]
    public async Task ImportAsync_MovesAFinishedChapterIntoASeriesFolderAndRemovesTheEmptiedFolders()
    {
        var source = Download("MangaDex (EN)", "Solo Leveling", "Official_Chapter 1.cbz");

        var result = await CreateService().ImportAsync();

        var destination = Path.Combine(_library, "Solo Leveling", "Official_Chapter 1.cbz");
        var imported = Assert.Single(result.Imported);
        Assert.Equal(source, imported.SourcePath);
        Assert.Equal(destination, imported.DestinationPath);
        Assert.False(File.Exists(source));
        Assert.Equal("page", ReadPage(destination));
        // No staging file is left behind next to the chapter.
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(destination)!));

        Assert.False(Directory.Exists(Path.Combine(_downloads, "MangaDex (EN)")));
        Assert.True(Directory.Exists(_downloads));
        Assert.Equal(2, result.RemovedFolders);
        Assert.Null(result.SkippedReason);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task ImportAsync_GroupsChaptersFromEverySourceBySeries()
    {
        Download("MangaDex (EN)", "Solo Leveling", "Chapter 1.cbz");
        Download("Asura Scans (EN)", "Solo Leveling", "Chapter 2.cbz");
        Download("Asura Scans (EN)", "Omniscient Reader", "Chapter 7.cbz");

        var result = await CreateService().ImportAsync();

        Assert.Equal(3, result.Imported.Count);
        Assert.Equal(
            new[] { "Chapter 1.cbz", "Chapter 2.cbz" },
            Directory.GetFiles(Path.Combine(_library, "Solo Leveling")).Select(Path.GetFileName).Order());
        Assert.True(File.Exists(Path.Combine(_library, "Omniscient Reader", "Chapter 7.cbz")));
        Assert.Empty(Directory.GetFileSystemEntries(_downloads));
    }

    [Fact]
    public async Task ImportAsync_CreatesNewSeriesFoldersInTheConfiguredImportFolder()
    {
        _settings.SuwayomiImportDirectory = "Manga";
        Download("Source", "Solo Leveling", "Chapter 1.cbz");

        var result = await CreateService().ImportAsync();

        Assert.Equal(
            Path.Combine(_library, "Manga", "Solo Leveling", "Chapter 1.cbz"),
            Assert.Single(result.Imported).DestinationPath);
    }

    [Fact]
    public async Task ImportAsync_JoinsAFolderNamedLikeTheSeriesIgnoringCaseAndPunctuation()
    {
        var existing = Directory.CreateDirectory(Path.Combine(_library, "solo-leveling")).FullName;
        Download("Source", "Solo Leveling", "Chapter 2.cbz");

        var result = await CreateService().ImportAsync();

        Assert.Equal(Path.Combine(existing, "Chapter 2.cbz"), Assert.Single(result.Imported).DestinationPath);
        Assert.False(Directory.Exists(Path.Combine(_library, "Solo Leveling")));
        // A folder match needs no library lookup.
        _seriesLibrary.Verify(
            l => l.GetAllSeriesFolderGroupsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ImportAsync_JoinsTheLargestLibraryFolderOfTheSeriesKnownByThatTitle()
    {
        var mainFolder = Directory.CreateDirectory(Path.Combine(_library, "Manhwa", "Na Honjaman Level Up")).FullName;
        var strayFolder = Directory.CreateDirectory(Path.Combine(_library, "Unsorted")).FullName;
        SetupLibrary(
            new[]
            {
                new SeriesSummaryDto
                {
                    Id = "s1",
                    Title = "Na Honjaman Level Up",
                    CanonicalTitle = "Na Honjaman Level Up",
                    Aliases = new List<string> { "Solo Leveling" }
                }
            },
            new[] { Folders("s1", mainFolder, strayFolder) });
        Download("Source", "Solo Leveling", "Chapter 3.cbz");
        Download("Source", "Solo Leveling", "Chapter 4.cbz");

        var result = await CreateService().ImportAsync();

        Assert.Equal(
            new[] { Path.Combine(mainFolder, "Chapter 3.cbz"), Path.Combine(mainFolder, "Chapter 4.cbz") },
            result.Imported.Select(chapter => chapter.DestinationPath));
        // The library is read once per run, not once per chapter.
        _seriesLibrary.Verify(
            l => l.GetAllSeriesFolderGroupsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ImportAsync_CreatesANewFolderWhenTheTitleBelongsToSeveralLibrarySeries()
    {
        var first = Directory.CreateDirectory(Path.Combine(_library, "A", "First")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_library, "B", "Second")).FullName;
        SetupLibrary(
            new[]
            {
                new SeriesSummaryDto { Id = "s1", Title = "First", Aliases = new List<string> { "Solo Leveling" } },
                new SeriesSummaryDto { Id = "s2", Title = "Second", Aliases = new List<string> { "Solo Leveling" } }
            },
            new[] { Folders("s1", first), Folders("s2", second) });
        Download("Source", "Solo Leveling", "Chapter 1.cbz");

        var result = await CreateService().ImportAsync();

        Assert.Equal(
            Path.Combine(_library, "Solo Leveling", "Chapter 1.cbz"),
            Assert.Single(result.Imported).DestinationPath);
    }

    [Fact]
    public async Task ImportAsync_NeverJoinsLibraryFoldersThatAreTheLibraryRootOrOutsideIt()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "elsewhere", "Solo Leveling")).FullName;
        SetupLibrary(
            new[] { new SeriesSummaryDto { Id = "s1", Title = "Solo Leveling" } },
            new[] { Folders("s1", _library, outside) });
        Download("Source", "Solo Leveling", "Chapter 1.cbz");

        var result = await CreateService().ImportAsync();

        Assert.Equal(
            Path.Combine(_library, "Solo Leveling", "Chapter 1.cbz"),
            Assert.Single(result.Imported).DestinationPath);
        Assert.Empty(Directory.GetFiles(outside));
    }

    [Fact]
    public async Task ImportAsync_FallsBackToANewFolderWhenTheLibraryCannotBeRead()
    {
        _seriesLibrary
            .Setup(l => l.GetSeriesSummariesAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        Download("Source", "Solo Leveling", "Chapter 1.cbz");

        var result = await CreateService().ImportAsync();

        Assert.Equal(
            Path.Combine(_library, "Solo Leveling", "Chapter 1.cbz"),
            Assert.Single(result.Imported).DestinationPath);
    }

    [Fact]
    public async Task ImportAsync_KeepsBothFilesWhenTheChapterNameIsTaken()
    {
        var existing = WriteCbz(Path.Combine(_library, "Solo Leveling", "Chapter 1.cbz"), "already in the library");
        Download("Source", "Solo Leveling", "Chapter 1.cbz");

        var result = await CreateService().ImportAsync();

        var destination = Path.Combine(_library, "Solo Leveling", "Chapter 1 (2).cbz");
        Assert.Equal(destination, Assert.Single(result.Imported).DestinationPath);
        Assert.Equal("already in the library", ReadPage(existing));
        Assert.Equal("page", ReadPage(destination));
    }

    [Fact]
    public async Task ImportAsync_LeavesArchivesThatAreStillBeingWrittenForTheNextRun()
    {
        var source = Download("Source", "Solo Leveling", "Chapter 1.cbz");
        _time = new FakeTimeProvider(File.GetLastWriteTimeUtc(source).AddSeconds(5));

        var result = await CreateService().ImportAsync();

        Assert.Empty(result.Imported);
        Assert.Equal(1, result.StillDownloading);
        Assert.True(File.Exists(source));
        Assert.Equal(0, result.RemovedFolders);
        Assert.False(Directory.Exists(Path.Combine(_library, "Solo Leveling")));
    }

    [Fact]
    public async Task ImportAsync_LeavesArchivesThatDoNotOpenInPlace()
    {
        // Suwayomi writes a CBZ's central directory last: a cut-off archive
        // is one it has not finished.
        var bytes = File.ReadAllBytes(WriteCbz(Path.Combine(_root, "whole.cbz"), new string('x', 4096)));
        var partial = Path.Combine(_downloads, "Source", "Solo Leveling", "Chapter 1.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        File.WriteAllBytes(partial, bytes[..(bytes.Length / 2)]);

        var result = await CreateService().ImportAsync();

        Assert.False(SuwayomiDownloadImportService.IsCompleteArchive(partial));
        Assert.Empty(result.Imported);
        Assert.Equal(1, result.Unreadable);
        Assert.True(File.Exists(partial));
        Assert.False(Directory.Exists(Path.Combine(_library, "Solo Leveling")));
    }

    [Fact]
    public async Task ImportAsync_StopsWithAPermissionErrorWhenAChapterCannotBeRead()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return; // File modes are not enforced there.
        }

        var chapter = Download("Source", "Solo Leveling", "Chapter 1.cbz");
        Download("Source", "Solo Leveling", "Chapter 2.cbz");
        File.SetUnixFileMode(chapter, UnixFileMode.None);
        try
        {
            var result = await CreateService().ImportAsync();

            Assert.True(result.PermissionDenied);
            Assert.Equal(1, result.Failed);
            Assert.Equal(0, result.Unreadable);
            Assert.Empty(result.Imported);
        }
        finally
        {
            File.SetUnixFileMode(chapter, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task ImportAsync_ReportsADownloadsFolderThatCannotBeListed()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return; // File modes are not enforced there.
        }

        Download("Source", "Solo Leveling", "Chapter 1.cbz");
        var source = Path.Combine(_downloads, "Source");
        File.SetUnixFileMode(source, UnixFileMode.None);
        try
        {
            var result = await CreateService().ImportAsync();

            Assert.NotNull(result.SkippedReason);
            Assert.Contains("could not be read", result.SkippedReason);
            Assert.Empty(result.Imported);
        }
        finally
        {
            File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task ImportAsync_LeavesArchivesOutsideASeriesFolderAndReportsChaptersSavedAsImages()
    {
        var stray = WriteCbz(Path.Combine(_downloads, "stray.cbz"));
        foreach (var page in new[] { "001.jpg", "002.jpg" })
        {
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(_downloads, "Source", "Solo Leveling", "Chapter 5")).FullName, page), "image");
            // Suwayomi's thumbnails sit one level down and are not chapters.
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(_downloads, "thumbnails")).FullName, page), "image");
        }

        var result = await CreateService().ImportAsync();

        Assert.Empty(result.Imported);
        Assert.Equal(1, result.WithoutSeriesFolder);
        Assert.Equal(1, result.ImageFolders);
        Assert.True(File.Exists(stray));
        Assert.Equal(0, result.RemovedFolders);
    }

    [Fact]
    public async Task ImportAsync_IgnoresStagingFiles()
    {
        var staging = WriteCbz(Path.Combine(_downloads, "Source", "Solo Leveling", ".tmp_0123456789abcdef.cbz"));

        var result = await CreateService().ImportAsync();

        Assert.Empty(result.Imported);
        Assert.True(File.Exists(staging));
    }

    private string PendingMovesPath => Path.Combine(_settings.ConfigDirectory, "suwayomi-import-pending.json");

    private void WritePendingMove(string stagingPath, string sourcePath)
    {
        Directory.CreateDirectory(_settings.ConfigDirectory);
        File.WriteAllText(PendingMovesPath, System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { StagingPath = stagingPath, SourcePath = sourcePath }
        }));
    }

    [Fact]
    public async Task ImportAsync_ClearsThePendingMoveJournalOnceAChapterIsPublished()
    {
        Download("Source", "Solo Leveling", "Chapter 1.cbz");

        var result = await CreateService().ImportAsync();

        Assert.Single(result.Imported);
        Assert.False(File.Exists(PendingMovesPath));
    }

    [Fact]
    public async Task ImportAsync_PublishesAChapterLeftStagedByAnInterruptedRun()
    {
        var source = Path.Combine(_downloads, "Source", "Solo Leveling", "Chapter 1.cbz");
        var staging = WriteCbz(Path.Combine(_library, "Solo Leveling", ".tmp_0123456789abcdef.suwayomi-import"), "staged");
        WritePendingMove(staging, source);

        var result = await CreateService().ImportAsync();

        var destination = Path.Combine(_library, "Solo Leveling", "Chapter 1.cbz");
        var imported = Assert.Single(result.Imported);
        Assert.Equal(destination, imported.DestinationPath);
        Assert.Equal("staged", ReadPage(destination));
        Assert.False(File.Exists(staging));
        Assert.False(File.Exists(PendingMovesPath));
    }

    [Fact]
    public async Task ImportAsync_DropsAStagedCopyWhoseDownloadIsStillInPlace()
    {
        var source = Download("Source", "Solo Leveling", "Chapter 1.cbz");
        var staging = WriteCbz(Path.Combine(_library, "Solo Leveling", ".tmp_0123456789abcdef.suwayomi-import"), "partial copy");
        WritePendingMove(staging, source);

        var result = await CreateService().ImportAsync();

        var destination = Path.Combine(_library, "Solo Leveling", "Chapter 1.cbz");
        Assert.Single(result.Imported);
        Assert.Equal("page", ReadPage(destination));
        Assert.False(File.Exists(source));
        Assert.Single(Directory.GetFiles(Path.Combine(_library, "Solo Leveling")));
        Assert.False(File.Exists(PendingMovesPath));
    }

    [Fact]
    public async Task ImportAsync_IgnoresPendingMovesOutsideTheLibrary()
    {
        var outside = WriteCbz(Path.Combine(_root, "elsewhere", ".tmp_0123456789abcdef.suwayomi-import"));
        WritePendingMove(outside, Path.Combine(_downloads, "Source", "Solo Leveling", "Chapter 1.cbz"));

        var result = await CreateService().ImportAsync();

        Assert.Empty(result.Imported);
        Assert.True(File.Exists(outside));
        Assert.False(File.Exists(PendingMovesPath));
    }

    [Fact]
    public async Task ImportAsync_RemovesFoldersItEmptiedAndStaleEmptyFoldersButKeepsNewOnes()
    {
        var now = DateTimeOffset.UtcNow;
        _time = new FakeTimeProvider(now);
        var source = Download("Source", "Solo Leveling", "Chapter 1.cbz");
        File.SetLastWriteTimeUtc(source, now.UtcDateTime.AddHours(-1));
        // Just created, perhaps for a download Suwayomi is about to write.
        var fresh = Directory.CreateDirectory(Path.Combine(_downloads, "Source", "Omniscient Reader")).FullName;
        // Left empty long ago.
        var stale = Directory.CreateDirectory(Path.Combine(_downloads, "Old Source", "Old Series")).FullName;
        Directory.SetLastWriteTimeUtc(stale, now.UtcDateTime.AddHours(-1));

        var result = await CreateService().ImportAsync();

        Assert.Single(result.Imported);
        Assert.False(Directory.Exists(Path.Combine(_downloads, "Source", "Solo Leveling")));
        Assert.True(Directory.Exists(fresh));
        Assert.False(Directory.Exists(Path.Combine(_downloads, "Old Source")));
        Assert.Equal(3, result.RemovedFolders);
    }

    [Fact]
    public async Task ImportAsync_DoesNothingWithoutADownloadsFolder()
    {
        _settings.SuwayomiDownloadsDirectory = "  ";

        var result = await CreateService().ImportAsync();

        Assert.True(result.NotConfigured);
        Assert.NotNull(result.SkippedReason);
        Assert.Empty(result.Imported);
    }

    [Fact]
    public async Task ImportAsync_ReportsAMissingDownloadsFolderWithoutThrowing()
    {
        _settings.SuwayomiDownloadsDirectory = Path.Combine(_root, "not-mounted");

        var result = await CreateService().ImportAsync();

        Assert.False(result.NotConfigured);
        Assert.Contains("does not exist", result.SkippedReason);
    }

    [Fact]
    public async Task ImportAsync_RefusesADownloadsFolderInsideTheLibrary()
    {
        var inside = Directory.CreateDirectory(Path.Combine(_library, "suwayomi")).FullName;
        var source = WriteCbz(Path.Combine(inside, "Source", "Solo Leveling", "Chapter 1.cbz"));
        _settings.SuwayomiDownloadsDirectory = inside;

        var result = await CreateService().ImportAsync();

        Assert.Contains("watched directory", result.SkippedReason);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void TryResolveDownloadsDirectory_AcceptsAnExistingFolderApartFromTheLibrary()
    {
        var accepted = SuwayomiDownloadImportService.TryResolveDownloadsDirectory(
            $" {_downloads}{Path.DirectorySeparatorChar} ", _settings, out var resolved, out var error);

        Assert.True(accepted, error);
        Assert.Equal(_downloads, resolved);
        Assert.Null(error);
    }

    [Fact]
    public void TryResolveDownloadsDirectory_RejectsRelativeAndOverlappingFolders()
    {
        var insideLibrary = Directory.CreateDirectory(Path.Combine(_library, "suwayomi")).FullName;
        var insideConfig = Directory.CreateDirectory(Path.Combine(_root, "config", "suwayomi")).FullName;
        var insideDuplicates = Directory.CreateDirectory(Path.Combine(_root, "duplicates", "suwayomi")).FullName;

        // _root holds the library itself.
        foreach (var folder in new[] { "suwayomi/downloads", insideLibrary, insideConfig, insideDuplicates, _root })
        {
            Assert.False(
                SuwayomiDownloadImportService.TryResolveDownloadsDirectory(folder, _settings, out _, out var error),
                folder);
            Assert.False(string.IsNullOrEmpty(error));
        }
    }

    [Fact]
    public void TryResolveImportDirectory_ResolvesAgainstTheWatchedDirectory()
    {
        Assert.True(SuwayomiDownloadImportService.TryResolveImportDirectory(null, _settings, out var root, out _));
        Assert.Equal(_library, root);

        Assert.True(SuwayomiDownloadImportService.TryResolveImportDirectory(" Manga ", _settings, out var relative, out _));
        Assert.Equal(Path.Combine(_library, "Manga"), relative);

        var absolute = Path.Combine(_library, "Manhwa");
        Assert.True(SuwayomiDownloadImportService.TryResolveImportDirectory(absolute, _settings, out var resolved, out _));
        Assert.Equal(absolute, resolved);
    }

    [Fact]
    public void TryResolveImportDirectory_RejectsFoldersOutsideTheWatchedDirectory()
    {
        foreach (var folder in new[] { Path.Combine("..", "elsewhere"), _downloads })
        {
            Assert.False(
                SuwayomiDownloadImportService.TryResolveImportDirectory(folder, _settings, out _, out var error),
                folder);
            Assert.Contains("inside the watched directory", error);
        }
    }
}
