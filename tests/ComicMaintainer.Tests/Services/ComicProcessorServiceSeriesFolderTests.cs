using System.IO.Compression;
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
/// With a folder format (<see cref="AppSettings.FolderFormat"/>) processing and renaming move a
/// file into its series folder directly under the watched folder: never over another file, with
/// the file store following it, and removing the folder it left only when nothing worth keeping
/// is left there.
/// </summary>
public sealed class ComicProcessorServiceSeriesFolderTests : IDisposable
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Join(Path.GetTempPath(), $"series_folder_tests_{Guid.NewGuid():N}");
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly List<ProcessingHistoryEntry> _history = new();
    private readonly AppSettings _settings;
    private BatchJobRegistry? _jobs;

    public ComicProcessorServiceSeriesFolderTests()
    {
        Directory.CreateDirectory(_root);
        _settings = new AppSettings
        {
            WatchedDirectory = _root,
            DuplicateDirectory = Path.Join(_root, "duplicates"),
            TempFileDirectory = Path.Join(_root, "temp"),
            FilenameFormat = "{series} - Chapter {issue}",
            FolderFormat = "{series}",
            IssueNumberPadding = 4,
            WatcherEnableRename = true,
            WatcherEnableNormalize = true
        };
    }

    public void Dispose() => TestIO.TryDeleteDirectory(_root);

    [Fact]
    public async Task ProcessFileAsync_FolderFormatSet_MovesTheFileIntoItsSeriesFolderAndRemovesTheEmptiedOne()
    {
        // Suwayomi saved "Solo Leveling: Ragnarok" as "Solo Leveling_ Ragnarok".
        var file = CreateArchive("Solo Leveling_ Ragnarok", "Solo Leveling_ Ragnarok - Chapter 0001.cbz", "Solo Leveling: Ragnarok", "1");
        File.WriteAllText(Path.Join(_root, "Solo Leveling_ Ragnarok", "cover.jpg"), "cover");
        var expected = Path.Join(_root, "Solo Leveling - Ragnarok", "Solo Leveling - Ragnarok - Chapter 0001.cbz");
        using var service = CreateService();

        Assert.True(await service.ProcessFileAsync(file));

        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(Path.Join(_root, "Solo Leveling_ Ragnarok")), "nothing but its cover was left");
        Assert.True(Directory.Exists(_root));
        _fileStore.Verify(s => s.UpdateFilePathAsync(file, expected, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
        _fileStore.Verify(s => s.MarkFileRenamedAsync(expected, true, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(s => s.MarkFileDuplicateAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(_history, entry => entry.Action == "Rename" && entry.Success
            && entry.BeforeFilename == "Solo Leveling_ Ragnarok/Solo Leveling_ Ragnarok - Chapter 0001.cbz"
            && entry.AfterFilename == "Solo Leveling - Ragnarok/Solo Leveling - Ragnarok - Chapter 0001.cbz");
    }

    [Fact]
    public async Task ProcessFileAsync_TargetTakenInTheSeriesFolder_FlagsADuplicateAndMovesNothing()
    {
        var file = CreateArchive("Saga (old)", "Saga 001.cbz", "Saga", "1");
        var taken = CreateArchive("Saga", "Saga - Chapter 0001.cbz", "Saga", "1");
        var takenBytes = File.ReadAllBytes(taken);
        using var service = CreateService();

        Assert.False(await service.ProcessFileAsync(file));

        Assert.True(File.Exists(file));
        Assert.Equal(takenBytes, File.ReadAllBytes(taken));
        _fileStore.Verify(s => s.MarkFileDuplicateAsync(file, true, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(s => s.UpdateFilePathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ProcessFileAsync_FolderStillHoldsOtherFiles_KeepsTheFolder()
    {
        var first = CreateArchive("Nightwing (2016-)", "Nightwing 001.cbz", "Nightwing", "1");
        CreateArchive("Nightwing (2016-)", "Nightwing 002.cbz", "Nightwing", "2");
        var notes = Path.Join(_root, "Nightwing (2016-)", "notes.txt");
        File.WriteAllText(notes, "mine");
        using var service = CreateService();

        Assert.True(await service.ProcessFileAsync(first));

        // Another comic is left: the folder stays without a full check.
        Assert.True(File.Exists(Path.Join(_root, "Nightwing", "Nightwing (2016) - Chapter 0001.cbz")));
        Assert.True(Directory.Exists(Path.Join(_root, "Nightwing (2016-)")));

        Assert.True(await service.ProcessFileAsync(Path.Join(_root, "Nightwing (2016-)", "Nightwing 002.cbz")));

        // Only the notes are left, and they are not disposable.
        Assert.True(File.Exists(Path.Join(_root, "Nightwing", "Nightwing (2016) - Chapter 0002.cbz")));
        Assert.Equal("mine", File.ReadAllText(notes));
    }

    [Fact]
    public async Task ProcessFileAsync_NoFolderFormat_LeavesTheFileInItsFolder()
    {
        _settings.FolderFormat = string.Empty;
        var file = CreateArchive("Saga (old)", "Saga 001.cbz", "Saga", "1");
        using var service = CreateService();

        Assert.True(await service.ProcessFileAsync(file));

        Assert.True(File.Exists(Path.Join(_root, "Saga (old)", "Saga - Chapter 0001.cbz")));
        Assert.False(Directory.Exists(Path.Join(_root, "Saga")));
    }

    [Fact]
    public async Task ProcessFileAsync_AlreadyInItsSeriesFolder_IsNotMoved()
    {
        var file = CreateArchive("Saga", "Saga - Chapter 0001.cbz", "Saga", "1");
        using var service = CreateService();

        Assert.True(await service.ProcessFileAsync(file));

        Assert.True(File.Exists(file));
        _fileStore.Verify(s => s.UpdateFilePathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ProcessFileAsync_LooseFileInTheWatchedFolder_MovesIntoItsSeriesFolderAndKeepsTheRoot()
    {
        var file = CreateArchive(string.Empty, "Saga 001.cbz", "Saga", "1");
        using var service = CreateService();

        Assert.True(await service.ProcessFileAsync(file));

        Assert.True(File.Exists(Path.Join(_root, "Saga", "Saga - Chapter 0001.cbz")));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task ProcessFileAsync_FileInTheDuplicatesFolder_StaysThere()
    {
        var file = CreateArchive(Path.Join("duplicates", "Saga"), "Saga 001.cbz", "Saga", "1");
        using var service = CreateService();

        Assert.True(await service.ProcessFileAsync(file));

        Assert.True(File.Exists(Path.Join(_root, "duplicates", "Saga", "Saga - Chapter 0001.cbz")));
        Assert.False(Directory.Exists(Path.Join(_root, "Saga")));
    }

    [Fact]
    public async Task ProcessFileAsync_SeriesFolderExistsInAnotherCase_UsesThatFolder()
    {
        var existing = Path.Join(_root, "x-men (2024)");
        Directory.CreateDirectory(existing);
        _settings.FolderFormat = "{series} ({volume_year})";
        var file = CreateArchive("X-Men (2024-)", "X-Men 001.cbz", "X-Men", "1", volumeYear: "2024");
        using var service = CreateService();

        Assert.True(await service.ProcessFileAsync(file));

        Assert.Equal(new[] { "x-men (2024)" }, Directory.EnumerateDirectories(_root)
            .Select(Path.GetFileName)
            .Where(name => name!.Contains("men", StringComparison.OrdinalIgnoreCase))
            .ToArray());
        Assert.True(File.Exists(Path.Join(existing, "X-Men (2024) - Chapter 0001.cbz")));
    }

    [Fact]
    public async Task ProcessFileAsync_FolderFormatRendersNoName_LeavesTheFileInItsFolder()
    {
        // "{series}" alone is required by the settings, but a format read from the
        // configuration file is not checked: one that renders nothing moves nothing.
        _settings.FolderFormat = "{volume_year}";
        var file = CreateArchive("Saga (old)", "Saga 001.cbz", "Saga", "1");
        using var service = CreateService();

        Assert.True(await service.ProcessFileAsync(file));

        Assert.True(File.Exists(Path.Join(_root, "Saga (old)", "Saga - Chapter 0001.cbz")));
    }

    [Fact]
    public async Task RenameFilesAsync_FolderFormatSet_MovesTheFileToo()
    {
        var file = CreateArchive("Monstress (2015-)", "Monstress 001.cbz", "Monstress", "1");
        var expected = Path.Join(_root, "Monstress", "Monstress (2015) - Chapter 0001.cbz");
        using var service = CreateService();

        var job = await WaitForJobAsync(await service.RenameFilesAsync(new[] { file }));

        Assert.Equal(1, job.ProcessedFiles);
        Assert.True(File.Exists(expected));
        Assert.False(Directory.Exists(Path.Join(_root, "Monstress (2015-)")));
        _fileStore.Verify(s => s.UpdateFilePathAsync(file, expected, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
    }

    private BatchJobRegistry Jobs => _jobs ??= TestServices.CreateBatchJobRegistry(
        new TestOptionsMonitor<AppSettings>(_settings),
        NullLogger<BatchJobRegistry>.Instance);

    private ComicProcessorService CreateService()
    {
        var history = new Mock<IProcessingHistoryService>();
        history.Setup(h => h.AddHistoryEntryAsync(It.IsAny<ProcessingHistoryEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessingHistoryEntry, CancellationToken>((entry, _) =>
            {
                lock (_history)
                {
                    _history.Add(entry);
                }
            })
            .Returns(Task.CompletedTask);
        return TestServices.CreateComicProcessor(
            new TestOptionsMonitor<AppSettings>(_settings),
            Mock.Of<ILogger<ComicProcessorService>>(),
            _fileStore.Object,
            history.Object,
            externalSeriesMetadata: Mock.Of<IExternalSeriesMetadataService>(),
            jobRegistry: Jobs);
    }

    private string CreateArchive(string folder, string fileName, string series, string number, string? volumeYear = null)
    {
        var directory = Path.Join(_root, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open()))
        {
            var volume = volumeYear is null ? string.Empty : $"<Volume>{volumeYear}</Volume>";
            writer.Write($"<?xml version=\"1.0\"?><ComicInfo><Series>{series}</Series><Number>{number}</Number><Title>Chapter {number}</Title>{volume}</ComicInfo>");
        }

        using (var writer = new StreamWriter(archive.CreateEntry("page001.jpg").Open()))
        {
            writer.Write("page");
        }

        return path;
    }

    private async Task<ProcessingJob> WaitForJobAsync(Guid jobId)
    {
        var deadline = DateTime.UtcNow + JobTimeout;
        while (true)
        {
            var job = Jobs.GetJob(jobId);
            if (job is { Status: not (JobStatus.Queued or JobStatus.Running) })
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
}
