using System.IO.Compression;
using System.Xml.Linq;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Loose files directly in the watched directory get their series from their own name or
/// metadata, never from the library root's folder name.
/// </summary>
public sealed class ComicProcessorServiceLibraryRootTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"watched_dir_{Guid.NewGuid():N}");
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IExternalSeriesMetadataService> _external = new();
    private readonly List<ProcessingHistoryEntry> _history = new();
    private readonly AppSettings _settings;

    public ComicProcessorServiceLibraryRootTests()
    {
        Directory.CreateDirectory(_root);
        _settings = new AppSettings
        {
            WatchedDirectory = _root,
            DuplicateDirectory = Path.Join(_root, "duplicates"),
            TempFileDirectory = Path.Join(Path.GetTempPath(), $"watched_dir_temp_{Guid.NewGuid():N}"),
            FilenameFormat = "{series} - Chapter {issue}",
            IssueNumberPadding = 4,
            WatcherEnableRename = true,
            WatcherEnableNormalize = true
        };
    }

    public void Dispose()
    {
        TestIO.TryDeleteDirectory(_root);
        TestIO.TryDeleteDirectory(_settings.TempFileDirectory);
    }

    [Fact]
    public async Task ProcessFileAsync_LooseFilesWithoutMetadata_TakeTheirSeriesFromTheirOwnNames()
    {
        var batman = CreateArchive(_root, "Batman 001.cbz", comicInfo: null);
        var superman = CreateArchive(_root, "Superman 001.cbz", comicInfo: null);
        using var service = CreateService();

        Assert.True(await service.ProcessFileAsync(batman));
        Assert.True(await service.ProcessFileAsync(superman));

        var renamedBatman = Path.Join(_root, "Batman - Chapter 0001.cbz");
        var renamedSuperman = Path.Join(_root, "Superman - Chapter 0001.cbz");
        Assert.True(File.Exists(renamedBatman));
        Assert.True(File.Exists(renamedSuperman));
        Assert.Equal("Batman", ReadComicInfo(renamedBatman, "Series"));
        Assert.Equal("Superman", ReadComicInfo(renamedSuperman, "Series"));
        _fileStore.Verify(s => s.MarkFileDuplicateAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Never);
        AssertTheRootNameWasNeverUsedAsASeries();
    }

    [Fact]
    public async Task ProcessFileAsync_LooseFileWithASeriesInItsMetadata_UsesTheMetadata()
    {
        var file = CreateArchive(_root, "scan_0001.cbz", "<ComicInfo><Series>Saga</Series><Number>1</Number><Title>Chapter 1</Title></ComicInfo>");
        using var service = CreateService();

        Assert.True(await service.ProcessFileAsync(file));

        Assert.True(File.Exists(Path.Join(_root, "Saga - Chapter 0001.cbz")));
        AssertTheRootNameWasNeverUsedAsASeries();
    }

    [Fact]
    public async Task ProcessFileAsync_LooseFileThatNamesNoSeries_IsNotRenamedAndSaysWhy()
    {
        var first = CreateArchive(_root, "001.cbz", comicInfo: null);
        var second = CreateArchive(_root, "Chapter 1.cbz", comicInfo: null);
        using var service = CreateService();

        Assert.False(await service.ProcessFileAsync(first));
        Assert.False(await service.ProcessFileAsync(second));

        // Both keep their names: neither is renamed after the root or "Unknown Series".
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal(2, Directory.EnumerateFiles(_root).Count());
        Assert.Null(ReadComicInfo(first, "Series"));
        Assert.Contains(_history, e => e.FilePath == first && e.Action == "Rename" && !e.Success
            && e.ErrorMessage!.StartsWith("Loose file in the library root", StringComparison.Ordinal));
        _fileStore.Verify(s => s.MarkFileDuplicateAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Never);
        AssertTheRootNameWasNeverUsedAsASeries();
    }

    [Fact]
    public async Task ProcessFileAsync_FileInASeriesFolder_StillTakesTheFolderName()
    {
        var folder = Path.Join(_root, "One Piece");
        Directory.CreateDirectory(folder);
        var file = CreateArchive(folder, "c1050.cbz", comicInfo: null);
        using var service = CreateService();

        Assert.True(await service.ProcessFileAsync(file));

        Assert.True(File.Exists(Path.Join(folder, "One Piece - Chapter 1050.cbz")));
    }

    [Fact]
    public async Task GetMetadataAsync_LooseFileWithoutComicInfo_ParsesTheSeriesFromItsName()
    {
        var file = CreateArchive(_root, "Batman #12 (2016).cbz", comicInfo: null);
        using var service = CreateService();

        var metadata = await service.GetMetadataAsync(file);

        Assert.Equal("Batman", metadata!.Series);
    }

    private void AssertTheRootNameWasNeverUsedAsASeries()
    {
        var looked = _external.Invocations
            .Where(i => i.Method.Name == nameof(IExternalSeriesMetadataService.LookupSeriesAsync))
            .Select(i => (string)i.Arguments[0])
            .ToList();
        Assert.DoesNotContain(looked, name => name.Contains("watched", StringComparison.OrdinalIgnoreCase));
    }

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
        return new ComicProcessorService(
            new TestOptionsMonitor<AppSettings>(_settings),
            Mock.Of<ILogger<ComicProcessorService>>(),
            _fileStore.Object,
            history.Object,
            externalSeriesMetadata: _external.Object);
    }

    private static string CreateArchive(string directory, string fileName, string? comicInfo)
    {
        var path = Path.Join(directory, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        if (comicInfo is not null)
        {
            using var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open());
            writer.Write(comicInfo);
        }

        using (var writer = new StreamWriter(archive.CreateEntry("page001.jpg").Open()))
        {
            writer.Write("page");
        }

        return path;
    }

    private static string? ReadComicInfo(string path, string element)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.Entries.FirstOrDefault(e => e.FullName.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        return XDocument.Load(stream).Root?.Element(element)?.Value;
    }
}
