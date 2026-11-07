using System.IO.Compression;
using System.Xml.Linq;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Runs files without a ComicInfo.xml through the real processor, which falls back to the shared
/// file-name issue parser. The old fallback took the first number in the name, so years, volumes
/// and numbers in the series name ended up in &lt;Number&gt;, the title and the new file name.
/// </summary>
public sealed class ComicProcessorServiceIssueNumberTests : IDisposable
{
    private readonly string _root;
    private readonly AppSettings _settings;
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly ComicProcessorService _service;

    public ComicProcessorServiceIssueNumberTests()
    {
        _root = Path.Join(Path.GetTempPath(), "cps-issue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _settings = new AppSettings
        {
            WatchedDirectory = _root,
            DuplicateDirectory = Path.Join(_root, "duplicates"),
            TempFileDirectory = Path.Join(_root, "temp"),
            FilenameFormat = AppSettings.DefaultFilenameFormat,
            IssueNumberPadding = 4
        };
        _service = new ComicProcessorService(
            new TestOptionsMonitor<AppSettings>(_settings),
            NullLogger<ComicProcessorService>.Instance,
            _fileStore.Object,
            Mock.Of<IProcessingHistoryService>());
    }

    public void Dispose()
    {
        _service.Dispose();
        TestIO.TryDeleteDirectory(_root);
    }

    [Theory]
    [InlineData("Batman", "Batman (2016) 005.cbz", "5", "Batman - Chapter 0005.cbz")]
    [InlineData("Series", "Vol.1 Ch.12.cbz", "12", "Series - Chapter 0012.cbz")]
    [InlineData("Spider-Man 2099", "Spider-Man 2099 012.cbz", "12", "Spider-Man 2099 - Chapter 0012.cbz")]
    public async Task ProcessFileAsync_NoComicInfo_WritesAndRenamesWithTheIssueNumberFromTheName(
        string folder, string fileName, string expectedNumber, string expectedFileName)
    {
        var path = WriteArchive(folder, fileName);

        var metadata = await _service.GetMetadataAsync(path);
        Assert.Equal(expectedNumber, metadata!.Issue);

        Assert.True(await _service.ProcessFileAsync(path));

        var renamed = Path.Join(_root, folder, expectedFileName);
        Assert.True(File.Exists(renamed), $"expected {expectedFileName}");
        Assert.False(File.Exists(path));
        var info = ReadComicInfo(renamed);
        Assert.Equal(expectedNumber, info.Element("Number")?.Value);
        Assert.Equal($"Chapter {expectedNumber}", info.Element("Title")?.Value);
    }

    [Fact]
    public async Task ProcessFileAsync_NoComicInfoAndOnlyAYearInTheName_WritesNoIssueNumber()
    {
        var path = WriteArchive("Series", "Series (2023).cbz");

        var metadata = await _service.GetMetadataAsync(path);
        Assert.Null(metadata!.Issue);

        Assert.True(await _service.ProcessFileAsync(path));

        var archive = Directory.GetFiles(Path.Join(_root, "Series"), "*.cbz").Single();
        Assert.Null(ReadComicInfo(archive).Element("Number"));
        Assert.DoesNotContain("2023", Path.GetFileName(archive).Replace("(2023)", string.Empty));
    }

    [Fact]
    public async Task GetSeriesMetadataAsync_NoComicInfo_UsesTheSharedParser()
    {
        var path = WriteArchive("Batman", "Batman (2016) 005.cbz");

        var metadata = await _service.GetSeriesMetadataAsync(path);

        Assert.Equal("5", metadata!.Issue);
    }

    private string WriteArchive(string folder, string fileName)
    {
        var directory = Path.Join(_root, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var page = zip.CreateEntry("page001.jpg").Open();
        page.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        return path;
    }

    private static XElement ReadComicInfo(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("ComicInfo.xml") ?? throw new InvalidOperationException("no ComicInfo.xml");
        using var stream = entry.Open();
        return XElement.Load(stream);
    }
}
