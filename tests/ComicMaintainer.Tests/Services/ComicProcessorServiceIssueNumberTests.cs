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
        _service = TestServices.CreateComicProcessor(
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
    // "(c2c)", "c2c" and a CRC tag are not chapter 2.
    [InlineData("Amazing Spider-Man", "Amazing Spider-Man 001 (1963) (c2c) (Digital).cbz", "1", "Amazing Spider-Man - Chapter 0001.cbz")]
    [InlineData("Hellboy", "Hellboy 001 c2c.cbz", "1", "Hellboy - Chapter 0001.cbz")]
    [InlineData("Series", "[Group] Series - 005 [5C2A9E01].cbz", "5", "Series - Chapter 0005.cbz")]
    // A number in the chapter title does not beat the issue number.
    [InlineData("One Piece", "One Piece 1001 - Wano Act 3.cbz", "1001", "One Piece - Chapter 1001.cbz")]
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessFileAsync_SeriesOnlyComicInfoAndAC2cTag_NeverUsesChapter2(bool normalize)
    {
        // With only <Series> in ComicInfo.xml the chapter comes from the file name: normalize
        // reads it with Parse, the rename safety net with ParseMarked. Both used to read the
        // "(c2c)" tag as chapter 2.
        _settings.WatcherEnableNormalize = normalize;
        _settings.WatcherEnableRename = true;
        var path = WriteArchive("Hellboy", "Hellboy 001 (2003) (c2c).cbz", "<ComicInfo><Series>Hellboy</Series></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));

        var archive = Directory.GetFiles(Path.Join(_root, "Hellboy"), "*.cbz").Single();
        Assert.DoesNotContain("0002", Path.GetFileName(archive));
        var info = ReadComicInfo(archive);
        if (normalize)
        {
            Assert.Equal("Hellboy - Chapter 0001.cbz", Path.GetFileName(archive));
            Assert.Equal("1", info.Element("Number")?.Value);
        }
        else
        {
            Assert.Null(info.Element("Number"));
        }
    }

    private string WriteArchive(string folder, string fileName, string? comicInfo = null)
    {
        var directory = Path.Join(_root, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        if (comicInfo is not null)
        {
            using var writer = new StreamWriter(zip.CreateEntry("ComicInfo.xml").Open());
            writer.Write(comicInfo);
        }

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
