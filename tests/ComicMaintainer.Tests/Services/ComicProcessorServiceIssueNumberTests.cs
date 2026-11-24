using System.IO.Compression;
using System.Xml.Linq;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
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
    // A year before the issue number is the volume's start year; a volume token is kept too.
    [InlineData("Batman", "Batman (2016) 005.cbz", "5", "Batman (2016) - Chapter 0005.cbz")]
    [InlineData("Series", "Vol.1 Ch.12.cbz", "12", "Series Vol. 1 - Chapter 0012.cbz")]
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

    [Fact]
    public async Task ProcessFileAsync_SameIssueOfTwoVolumesInOneFolder_RenamesBothAndFlagsNeither()
    {
        // American comics restart their numbering with each volume: Green Lantern #1 of 1990
        // and #1 of 2005 are different issues, not duplicates.
        var first = WriteArchive("Green Lantern", "GL 001 (a).cbz", VolumeInfo("1990", "1"));
        var second = WriteArchive("Green Lantern", "GL 001 (b).cbz", VolumeInfo("2005", "1"));

        Assert.True(await _service.ProcessFileAsync(first));
        Assert.True(await _service.ProcessFileAsync(second));

        Assert.Equal(
            new[] { "Green Lantern (1990) - Chapter 0001.cbz", "Green Lantern (2005) - Chapter 0001.cbz" },
            Directory.GetFiles(Path.Join(_root, "Green Lantern")).Select(Path.GetFileName).Order().ToArray());
        _fileStore.Verify(
            f => f.MarkFileDuplicateAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessFileAsync_SameIssueOfTheSameVolume_IsStillADuplicate()
    {
        var first = WriteArchive("Green Lantern", "a.cbz", VolumeInfo("1990", "1"));
        var second = WriteArchive("Green Lantern", "b.cbz", VolumeInfo("1990", "1"));

        Assert.True(await _service.ProcessFileAsync(first));
        await _service.ProcessFileAsync(second);

        Assert.True(File.Exists(Path.Join(_root, "Green Lantern", "Green Lantern (1990) - Chapter 0001.cbz")));
        _fileStore.Verify(f => f.MarkFileDuplicateAsync(second, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_TemplateWithItsOwnVolumePlaceholder_IsNotGivenASecondOne()
    {
        _settings.FilenameFormat = "{series} v{volume} #{issue}";
        var path = WriteArchive("Green Lantern", "x.cbz", VolumeInfo("3", "1"));

        Assert.True(await _service.ProcessFileAsync(path));

        Assert.True(File.Exists(Path.Join(_root, "Green Lantern", "Green Lantern v3 #0001.cbz")));
    }

    [Fact]
    public async Task ProcessFileAsync_SeriesNamedForItsVolume_StoresTheStartYearOutsideTheArchive()
    {
        // "The Astonishing X-Men (1995)": 1995 is when volume 1 started, not issue #12's date.
        // <Volume> holds the volume number, so the start year is kept in the database.
        var path = WriteArchive("The Astonishing X-Men (1995)", "The Astonishing X-Men 012.cbz",
            "<ComicInfo><Series>The Astonishing X-Men (1995)</Series><Number>12</Number></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));

        var archive = Directory.GetFiles(Path.Join(_root, "The Astonishing X-Men (1995)")).Single();
        Assert.Equal("The Astonishing X-Men (1995) - Chapter 0012.cbz", Path.GetFileName(archive));
        var info = ReadComicInfo(archive);
        Assert.Null(info.Element("Volume"));
        Assert.Null(info.Element("Year"));
        _fileStore.Verify(f => f.SetVolumeYearAsync(path, 1995, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_VolumeNumberAndStartYear_GoToVolumeAndTheDatabase()
    {
        var path = WriteArchive("Green Lantern (1990)", "Green Lantern v3 001.cbz",
            "<ComicInfo><Series>Green Lantern</Series><Number>1</Number></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));

        var archive = Directory.GetFiles(Path.Join(_root, "Green Lantern (1990)")).Single();
        Assert.Equal("Green Lantern Vol. 3 - Chapter 0001.cbz", Path.GetFileName(archive));
        Assert.Equal("3", ReadComicInfo(archive).Element("Volume")?.Value);
        _fileStore.Verify(f => f.SetVolumeYearAsync(path, 1990, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_StoredStartYear_NamesAFileWithNoVolumeNumber_AndIsNotStoredAgain()
    {
        var path = WriteArchive("Saga", "Saga 001.cbz", "<ComicInfo><Series>Saga</Series><Number>1</Number></ComicInfo>");
        _fileStore.Setup(f => f.GetFileAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicFile { FilePath = path, Metadata = new ComicMetadata { VolumeYear = 2012 } });

        Assert.True(await _service.ProcessFileAsync(path));

        Assert.True(File.Exists(Path.Join(_root, "Saga", "Saga (2012) - Chapter 0001.cbz")));
        _fileStore.Verify(f => f.SetVolumeYearAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    // <Volume> holds only the volume number: a start year there moves to the database, and the
    // element is removed when no name gives the number...
    [InlineData("1990", "x.cbz", null, "Green Lantern (1990) - Chapter 0001.cbz")]
    [InlineData("(1990)", "x.cbz", null, "Green Lantern (1990) - Chapter 0001.cbz")]
    // ...or replaced by the number a name gives.
    [InlineData("1990", "Green Lantern v3 001.cbz", "3", "Green Lantern Vol. 3 - Chapter 0001.cbz")]
    public async Task ProcessFileAsync_YearInVolume_MovesToTheStartYear_LeavingOnlyANumber(
        string volume, string fileName, string? expectedVolume, string expectedFileName)
    {
        var path = WriteArchive("Green Lantern", fileName, VolumeInfo(volume, "1"));

        Assert.True(await _service.ProcessFileAsync(path));

        var archive = Directory.GetFiles(Path.Join(_root, "Green Lantern")).Single();
        Assert.Equal(expectedFileName, Path.GetFileName(archive));
        Assert.Equal(expectedVolume, ReadComicInfo(archive).Element("Volume")?.Value);
        _fileStore.Verify(f => f.SetVolumeYearAsync(path, 1990, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_VolumeNumberAlreadySet_IsKept()
    {
        var path = WriteArchive("Green Lantern", "Green Lantern v3 001.cbz", VolumeInfo("2", "1"));

        Assert.True(await _service.ProcessFileAsync(path));

        var archive = Directory.GetFiles(Path.Join(_root, "Green Lantern")).Single();
        Assert.Equal("2", ReadComicInfo(archive).Element("Volume")?.Value);
    }

    [Fact]
    public async Task ProcessFileAsync_VolumesNamedByTheirStartYears_EachGetTheirOwnVolume()
    {
        // Volume 1 started in 1995, volume 2 in 1999; both have a #1.
        var first = WriteArchive("The Astonishing X-Men", "The Astonishing X-Men (1995) 001.cbz");
        var second = WriteArchive("The Astonishing X-Men", "The Astonishing X-Men (1999) 001.cbz");

        Assert.True(await _service.ProcessFileAsync(first));
        Assert.True(await _service.ProcessFileAsync(second));

        var folder = Path.Join(_root, "The Astonishing X-Men");
        Assert.True(File.Exists(Path.Join(folder, "The Astonishing X-Men (1995) - Chapter 0001.cbz")));
        Assert.True(File.Exists(Path.Join(folder, "The Astonishing X-Men (1999) - Chapter 0001.cbz")));
        _fileStore.Verify(f => f.SetVolumeYearAsync(first, 1995, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.SetVolumeYearAsync(second, 1999, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(
            f => f.MarkFileDuplicateAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("<Volume>2</Volume>", "2")]
    [InlineData("<Volume>Annual</Volume>", "Annual")]
    [InlineData("<Volume>-1</Volume>", "-1")]
    public async Task ProcessFileAsync_KeepsAVolumeAlreadySet_AndFillsOnlyAnUnsetOne(string volumeElement, string expected)
    {
        var path = WriteArchive("The Astonishing X-Men (1995)", "x.cbz",
            $"<ComicInfo><Series>The Astonishing X-Men (1995)</Series><Number>1</Number>{volumeElement}<Year>1995</Year></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));

        var info = ReadComicInfo(Directory.GetFiles(Path.Join(_root, "The Astonishing X-Men (1995)")).Single());
        Assert.Equal(expected, info.Element("Volume")?.Value);
        Assert.Equal("1995", info.Element("Year")?.Value);
    }

    [Fact]
    public async Task Normalize_OtherwiseNormalizedFileMissingItsVolume_IsWrittenWithIt()
    {
        // Series, number and title are already normalized: only the volume is missing.
        _settings.WatcherEnableRename = false;
        var path = WriteArchive("The Astonishing X-Men (1995)", "The Astonishing X-Men (1995) - Chapter 0001.cbz",
            "<ComicInfo><Series>The Astonishing X-Men (1995)</Series><Number>1</Number><Title>Chapter 1</Title></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));

        Assert.Null(ReadComicInfo(path).Element("Volume"));
        _fileStore.Verify(f => f.SetVolumeYearAsync(path, 1995, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_AlreadyRenamedFile_TakesTheVolumeAndCoverYearFromTheFileNameLeftInItsTitle()
    {
        // Renamed before volumes were named: only the title still holds the original file name.
        var path = WriteArchive("All-New X-Men", "All-New X-Men - Chapter 0008.cbz",
            "<ComicInfo><Series>All-New X-Men</Series><Number>8</Number><Title>All New X-Men v2 008 (2016)</Title></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));

        var archive = Directory.GetFiles(Path.Join(_root, "All-New X-Men")).Single();
        Assert.Equal("All-New X-Men Vol. 2 - Chapter 0008.cbz", Path.GetFileName(archive));
        var info = ReadComicInfo(archive);
        Assert.Equal("2", info.Element("Volume")?.Value);
        Assert.Equal("2016", info.Element("Year")?.Value);
        // Its volume and year read, the old file name gives way to a generated title, a
        // placeholder ComicVine's issue title replaces.
        Assert.Equal("Chapter 8", info.Element("Title")?.Value);
    }

    [Theory]
    // Old file names left as titles without the series, as found in a real library.
    [InlineData("Nightwing", "10", "v4 010 (2018)", "4", "2018", "Nightwing Vol. 4 - Chapter 0010.cbz")]
    [InlineData("House of M", "1", "v1 #1", "1", null, "House of M Vol. 1 - Chapter 0001.cbz")]
    [InlineData("I Hate Fairyland", "1", "001 (2015)", null, "2015", "I Hate Fairyland - Chapter 0001.cbz")]
    [InlineData("I Hate Fairyland", "4", "z-issue 004 (2016)", null, "2016", "I Hate Fairyland - Chapter 0004.cbz")]
    [InlineData("I Hate Fairyland", "5", "#5 (2016)", null, "2016", "I Hate Fairyland - Chapter 0005.cbz")]
    [InlineData("Batwoman", "0", "v1 000", "1", null, "Batwoman Vol. 1 - Chapter 0000.cbz")]
    [InlineData("Batwoman", "3", "v2 003 (2014).", "2", "2014", "Batwoman Vol. 2 - Chapter 0003.cbz")]
    [InlineData("Nightwing", "12.5", "v3 012.5 (2013)", "3", "2013", "Nightwing Vol. 3 - Chapter 0012.5.cbz")]
    public async Task ProcessFileAsync_AnOldFileNameWithoutTheSeries_GivesItsVolumeYearAndTitle(
        string series, string number, string title, string? volume, string? year, string expectedName)
    {
        var path = WriteArchive(series, $"{series} - Chapter x.cbz",
            $"<ComicInfo><Series>{series}</Series><Number>{number}</Number><Title>{title}</Title><Year>1970</Year></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path, forceReprocess: true));

        var archive = Directory.GetFiles(Path.Join(_root, series)).Single();
        Assert.Equal(expectedName, Path.GetFileName(archive));
        var info = ReadComicInfo(archive);
        Assert.Equal(volume, info.Element("Volume")?.Value);
        Assert.Equal(year ?? "1970", info.Element("Year")?.Value);
        Assert.Equal($"Chapter {number}", info.Element("Title")?.Value);
    }

    [Theory]
    // An old file name naming another issue: the tags are trusted. <Number> stays, the title
    // becomes a placeholder, and the other issue's cover year is not read. A volume runs across
    // its issues, so it fills an empty <Volume>; one already set is kept.
    [InlineData("I Hate Fairyland", "2", null, "Vol. 2 003 (2023)", "2", "I Hate Fairyland Vol. 2 - Chapter 0002.cbz")]
    [InlineData("Nightwing", "10", null, "v4 011 (2018)", "4", "Nightwing Vol. 4 - Chapter 0010.cbz")]
    [InlineData("Nightwing", "10", "3", "v4 010 (2018)", "3", "Nightwing Vol. 3 - Chapter 0010.cbz")]
    [InlineData("All-New X-Men", "8", "1", "All New X-Men v2 008 (2016)", "1", "All-New X-Men Vol. 1 - Chapter 0008.cbz")]
    public async Task ProcessFileAsync_AnOldFileNameThatDisagreesWithTheTags_TrustsTheTags(
        string series, string number, string? volume, string title, string expectedVolume, string expectedName)
    {
        var volumeTag = volume is null ? string.Empty : $"<Volume>{volume}</Volume>";
        var path = WriteArchive(series, $"{series} - Chapter x.cbz",
            $"<ComicInfo><Series>{series}</Series><Number>{number}</Number>{volumeTag}<Title>{title}</Title><Year>1970</Year></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path, forceReprocess: true));

        var archive = Directory.GetFiles(Path.Join(_root, series)).Single();
        Assert.Equal(expectedName, Path.GetFileName(archive));
        var info = ReadComicInfo(archive);
        Assert.Equal(number, info.Element("Number")?.Value);
        Assert.Equal(expectedVolume, info.Element("Volume")?.Value);
        Assert.Equal("1970", info.Element("Year")?.Value);
        Assert.Equal($"Chapter {number}", info.Element("Title")?.Value);
    }

    [Fact]
    public async Task ProcessFileAsync_AnOldFileNameWithAShortenedSeriesName_GivesItsVolumeYearAndTitle()
    {
        const string series = "Michael Turner's Soulfire";
        var path = WriteArchive(series, $"{series} - Chapter x.cbz",
            $"<ComicInfo><Series>{series}</Series><Number>3</Number><Title>Soulfire Vol 1 03 (of 10) (2011)</Title><Year>1970</Year></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path, forceReprocess: true));

        var archive = Directory.GetFiles(Path.Join(_root, series)).Single();
        Assert.Equal($"{series} Vol. 1 - Chapter 0003.cbz", Path.GetFileName(archive));
        var info = ReadComicInfo(archive);
        Assert.Equal("1", info.Element("Volume")?.Value);
        Assert.Equal("2011", info.Element("Year")?.Value);
        Assert.Equal("Chapter 3", info.Element("Title")?.Value);
    }

    [Fact]
    public async Task ProcessFileAsync_AnOldFileNameWithAnAliasOfTheSeries_GivesItsVolumeYearAndTitle()
    {
        const string series = "The Amazing Spider-Man";
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "amazingspiderman",
            CanonicalTitle = series,
            ResolvedSeriesName = series,
            Aliases = ["ASM"],
            UserAliases = ["Amazing Spidey"]
        };
        var cache = new Mock<ISeriesMetadataCacheService>();
        cache.Setup(c => c.GetByTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(record);
        using var service = TestServices.CreateComicProcessor(
            new TestOptionsMonitor<AppSettings>(_settings),
            NullLogger<ComicProcessorService>.Instance,
            _fileStore.Object,
            Mock.Of<IProcessingHistoryService>(),
            seriesMetadataCache: cache.Object);
        var asm = WriteArchive(series, $"{series} - Chapter x.cbz",
            $"<ComicInfo><Series>{series}</Series><Number>3</Number><Title>ASM v3 003 (2014)</Title><Year>1970</Year></ComicInfo>");

        Assert.True(await service.ProcessFileAsync(asm, forceReprocess: true));

        var info = ReadComicInfo(Directory.GetFiles(Path.Join(_root, series)).Single());
        Assert.Equal("3", info.Element("Volume")?.Value);
        Assert.Equal("2014", info.Element("Year")?.Value);
        Assert.Equal("Chapter 3", info.Element("Title")?.Value);
    }

    [Fact]
    public async Task Normalize_TheSeriesAliasesCannotBeRead_KeepsTheTitle()
    {
        _settings.WatcherEnableRename = false;
        var cache = new Mock<ISeriesMetadataCacheService>();
        cache.Setup(c => c.GetByTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));
        using var service = TestServices.CreateComicProcessor(
            new TestOptionsMonitor<AppSettings>(_settings),
            NullLogger<ComicProcessorService>.Instance,
            _fileStore.Object,
            Mock.Of<IProcessingHistoryService>(),
            seriesMetadataCache: cache.Object);
        var path = WriteArchive("Batman", "Batman - Chapter x.cbz",
            "<ComicInfo><Series>Batman</Series><Number>3</Number><Title>TDK 003 (2014)</Title></ComicInfo>");

        await service.ProcessFileAsync(path, forceReprocess: true);

        Assert.Equal("TDK 003 (2014)", ReadComicInfo(path).Element("Title")?.Value);
    }

    [Fact]
    public async Task ProcessFileAsync_AnOldFileNameOfThisIssueAndVolume_StillGivesItsYear()
    {
        var path = WriteArchive("Nightwing", "Nightwing - Chapter x.cbz",
            "<ComicInfo><Series>Nightwing</Series><Number>10</Number><Volume>4</Volume><Title>v4 010 (2018)</Title><Year>1970</Year></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path, forceReprocess: true));

        var info = ReadComicInfo(Directory.GetFiles(Path.Join(_root, "Nightwing")).Single());
        Assert.Equal("4", info.Element("Volume")?.Value);
        Assert.Equal("2018", info.Element("Year")?.Value);
    }

    [Theory]
    // A story title, or one with words a file name would not hold, stays.
    [InlineData("10", "The Hunt for Robin, Part 10")]
    [InlineData("3", "Vol. 2 003 (2023) Variant Edition")]
    [InlineData("1", ".I Hate Image Special Edition (2020)")]
    public async Task Normalize_ATitleThatIsNotAnOldFileName_IsKept(string number, string title)
    {
        _settings.WatcherEnableRename = false;
        var path = WriteArchive("Nightwing", "Nightwing - Chapter x.cbz",
            $"<ComicInfo><Series>Nightwing</Series><Number>{number}</Number><Title>{title}</Title></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path, forceReprocess: true));

        Assert.Equal(title, ReadComicInfo(path).Element("Title")?.Value);
    }

    [Fact]
    public async Task Normalize_TheVolumeNumberTaggedAsTheIssue_IsCorrectedFromTheFileName()
    {
        // A tagger wrote every issue of "Vol. 2" as Number 2: the file name's issue wins.
        _settings.WatcherEnableRename = false;
        var path = WriteArchive("I Hate Fairyland", "Vol. 2 004 (2023).cbz",
            "<ComicInfo><Series>I Hate Fairyland</Series><Number>2</Number><Volume>2</Volume><Title>Chapter 2</Title><Year>2023</Year></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));

        var info = ReadComicInfo(path);
        Assert.Equal(("4", "Chapter 4", "2"), (info.Element("Number")?.Value, info.Element("Title")?.Value, info.Element("Volume")?.Value));
    }

    [Fact]
    public async Task PreviewProcessing_TheVolumeNumberTaggedAsTheIssue_ShowsTheCorrection()
    {
        // Otherwise normalized, so only the mistake makes it one to rewrite.
        _settings.WatcherEnableRename = false;
        var path = WriteArchive("I Hate Fairyland", "Vol. 2 004 (2023).cbz",
            "<ComicInfo><Series>I Hate Fairyland</Series><Number>2</Number><Volume>2</Volume><Title>Chapter 2</Title></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));
        Assert.True(await _service.ProcessFileAsync(path, forceReprocess: true));

        Assert.Equal("4", ReadComicInfo(path).Element("Number")?.Value);
    }

    [Theory]
    [InlineData("Vol. 2 002 (2022).cbz", "2")] // the second issue of Vol. 2: right as it is
    [InlineData("Vol. 3 004 (2023).cbz", "2")] // the file name names another volume than the tags
    [InlineData("004 (2023).cbz", "2")] // no volume in the file name to show the mistake
    [InlineData("Vol. 2 (2023).cbz", "2")] // no issue in the file name
    [InlineData("I Hate Fairyland 2 Vol. 2 (2023).cbz", "2")] // a collected edition: no issue after the volume
    [InlineData("Saga Vol. 2 - Chapters 7-12.cbz", "2")] // ...nor a number from its title
    public async Task Normalize_AnIssueThatRepeatsTheVolume_IsKept_WithoutTheFileNameShowingTheMistake(string fileName, string expected)
    {
        _settings.WatcherEnableRename = false;
        var path = WriteArchive("I Hate Fairyland", fileName,
            "<ComicInfo><Series>I Hate Fairyland</Series><Number>2</Number><Volume>2</Volume><Title>Chapter 2</Title></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path, forceReprocess: true));

        Assert.Equal(expected, ReadComicInfo(path).Element("Number")?.Value);
    }

    [Theory]
    [InlineData("Issue #40.cbz", "Issue #40", "40")]
    [InlineData("#6.cbz", "#6", "6")]
    [InlineData("Green Lantern - New Guardians - Issue 40.cbz", "Issue 40", "40")]
    public async Task Normalize_ANumberTheFileNameAndTitleBothContradict_IsCorrected(string fileName, string title, string expected)
    {
        // A downloader numbered the issues by their place in its list (after an Issue #0).
        _settings.WatcherEnableRename = false;
        var path = WriteArchive("Green Lantern - New Guardians", fileName,
            $"<ComicInfo><Series>Green Lantern - New Guardians</Series><Number>41</Number><Title>{title}</Title></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));

        var info = ReadComicInfo(path);
        Assert.Equal((expected, $"Chapter {expected}"), (info.Element("Number")?.Value, info.Element("Title")?.Value));
    }

    [Theory]
    [InlineData("Issue #40.cbz", "Issue #39")] // the title names another number
    [InlineData("Issue #40.cbz", "The Last Lantern")] // a real title
    [InlineData("Issue #40.cbz", "")] // no title to agree
    [InlineData("40.cbz", "Issue #40")] // the file name marks no number
    [InlineData("Issue #41.cbz", "Issue #41")] // they agree with the tags
    // This application's own name and title, from before the number was corrected elsewhere.
    [InlineData("Green Lantern - New Guardians - Chapter 0040.cbz", "Chapter 40")]
    public async Task Normalize_ANumberOnlyOneOtherSourceContradicts_IsKept(string fileName, string title)
    {
        _settings.WatcherEnableRename = false;
        var path = WriteArchive("Green Lantern - New Guardians", fileName,
            $"<ComicInfo><Series>Green Lantern - New Guardians</Series><Number>41</Number><Title>{title}</Title></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path, forceReprocess: true));

        Assert.Equal("41", ReadComicInfo(path).Element("Number")?.Value);
    }

    [Fact]
    public async Task Normalize_TagsNamingAComicVineIssue_KeepTheirNumber()
    {
        _settings.WatcherEnableRename = false;
        var path = WriteArchive("Green Lantern - New Guardians", "Issue #40.cbz",
            "<ComicInfo><Series>Green Lantern - New Guardians</Series><Number>41</Number><Title>Issue #40</Title><Notes>[CVDB12345]</Notes></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path, forceReprocess: true));

        Assert.Equal("41", ReadComicInfo(path).Element("Number")?.Value);
    }

    [Fact]
    public async Task Normalize_ALockedNumberTheFileNameAndTitleContradict_IsKept()
    {
        _settings.WatcherEnableRename = false;
        var path = WriteArchive("Green Lantern - New Guardians", "Issue #40.cbz",
            "<ComicInfo><Series>Green Lantern - New Guardians</Series><Number>41</Number><Title>Issue #40</Title></ComicInfo>");
        _fileStore.Setup(f => f.GetFileAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(new ComicFile
        {
            FilePath = path,
            Metadata = new ComicMetadata { Issue = "41", IsUserEdited = true, UserLockedFieldsMask = (long)ComicMetadataFieldFlags.Issue }
        });

        Assert.True(await _service.ProcessFileAsync(path, forceReprocess: true));

        Assert.Equal("41", ReadComicInfo(path).Element("Number")?.Value);
    }

    [Fact]
    public async Task ProcessFileAsync_ANumberTheFileNameAndTitleContradict_IsRenamedByTheirNumber()
    {
        var path = WriteArchive("Green Lantern - New Guardians", "Issue #40.cbz",
            "<ComicInfo><Series>Green Lantern - New Guardians</Series><Number>41</Number><Title>Issue #40</Title></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));

        var renamed = Directory.GetFiles(Path.Join(_root, "Green Lantern - New Guardians")).Single();
        Assert.EndsWith("Chapter 0040.cbz", renamed);
    }

    [Fact]
    public async Task Normalize_ALockedIssueThatRepeatsTheVolume_IsKept()
    {
        _settings.WatcherEnableRename = false;
        var path = WriteArchive("I Hate Fairyland", "Vol. 2 004 (2023).cbz",
            "<ComicInfo><Series>I Hate Fairyland</Series><Number>2</Number><Volume>2</Volume><Title>Chapter 2</Title></ComicInfo>");
        _fileStore.Setup(f => f.GetFileAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(new ComicFile
        {
            FilePath = path,
            Metadata = new ComicMetadata { Issue = "2", IsUserEdited = true, UserLockedFieldsMask = (long)ComicMetadataFieldFlags.Issue }
        });

        Assert.True(await _service.ProcessFileAsync(path, forceReprocess: true));

        Assert.Equal("2", ReadComicInfo(path).Element("Number")?.Value);
    }

    [Fact]
    public async Task Normalize_ALockedOldFileNameTitle_IsKept()
    {
        _settings.WatcherEnableRename = false;
        var path = WriteArchive("Nightwing", "Nightwing - Chapter x.cbz",
            "<ComicInfo><Series>Nightwing</Series><Number>10</Number><Title>v4 010 (2018)</Title></ComicInfo>");
        _fileStore.Setup(f => f.GetFileAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(new ComicFile
        {
            FilePath = path,
            Metadata = new ComicMetadata { Title = "v4 010 (2018)", IsUserEdited = true, UserLockedFieldsMask = (long)ComicMetadataFieldFlags.Title }
        });

        Assert.True(await _service.ProcessFileAsync(path, forceReprocess: true));

        Assert.Equal("v4 010 (2018)", ReadComicInfo(path).Element("Title")?.Value);
    }

    [Fact]
    public async Task ProcessFileAsync_UnknownYear1970_IsReplacedByTheCoverYearInTheTitle()
    {
        var path = WriteArchive("All-New X-Men", "All-New X-Men - Chapter 0009.cbz",
            "<ComicInfo><Series>All-New X-Men</Series><Number>9</Number><Title>All New X-Men v2 009 (2016)</Title><Year>1970</Year></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));

        var archive = Directory.GetFiles(Path.Join(_root, "All-New X-Men")).Single();
        Assert.Equal("2016", ReadComicInfo(archive).Element("Year")?.Value);
    }

    [Fact]
    public async Task Normalize_UnknownYear1970_IsKeptWhenNoNameGivesACoverYear()
    {
        _settings.WatcherEnableRename = false;
        var path = WriteArchive("Saga", "Saga - Chapter 0001.cbz",
            "<ComicInfo><Series>Saga</Series><Number>1</Number><Title>Chapter 1</Title><Year>1970</Year></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));

        Assert.Equal("1970", ReadComicInfo(path).Element("Year")?.Value);
        Assert.Equal(path, Directory.GetFiles(Path.Join(_root, "Saga")).Single());
    }

    [Fact]
    public async Task ProcessFileAsync_ATieInNamedInTheFileName_KeepsItsSuffix_AndIsNotIssue1()
    {
        // Marvel's Monsters Unleashed tie-in, numbered #1.MU inside All-New X-Men Vol. 2.
        var issue1 = WriteArchive("All-New X-Men", "All New X-Men v2 001 (2016).cbz",
            "<ComicInfo><Series>All-New X-Men</Series><Number>1</Number><Volume>2</Volume></ComicInfo>");
        var tieIn = WriteArchive("All-New X-Men", "All New X-Men v2 001.MU (2017).cbz",
            "<ComicInfo><Series>All-New X-Men</Series><Number>1</Number><Volume>2</Volume></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(issue1));
        Assert.True(await _service.ProcessFileAsync(tieIn));

        var folder = Path.Join(_root, "All-New X-Men");
        Assert.True(File.Exists(Path.Join(folder, "All-New X-Men Vol. 2 - Chapter 0001.cbz")));
        var renamed = Path.Join(folder, "All-New X-Men Vol. 2 - Chapter 0001.MU.cbz");
        Assert.True(File.Exists(renamed), "the tie-in is named apart from issue #1");
        Assert.Equal("1.MU", ReadComicInfo(renamed).Element("Number")?.Value);
        _fileStore.Verify(f => f.MarkFileDuplicateAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    // Already renamed: only the old file name left in the title still says ".MU".
    [InlineData("1", "All New X-Men v2 001.MU (2017)", "1.MU")]
    // A title naming another number, or a story title, changes nothing.
    [InlineData("2", "All New X-Men v2 001.MU (2017)", "2")]
    [InlineData("1", "Monsters Unleashed", "1")]
    public async Task Normalize_ATieInSuffixLeftInTheTitle_IsWrittenIntoNumber(string number, string title, string expected)
    {
        _settings.WatcherEnableRename = false;
        var path = WriteArchive("All-New X-Men", "All-New X-Men - Chapter 0001.cbz",
            $"<ComicInfo><Series>All-New X-Men</Series><Number>{number}</Number><Title>{title}</Title></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));

        Assert.Equal(expected, ReadComicInfo(path).Element("Number")?.Value);
    }

    [Fact]
    public async Task ProcessFileAsync_RenameOnly_StillNamesTheVolumeFromTheTitle()
    {
        _settings.WatcherEnableNormalize = false;
        var path = WriteArchive("All-New X-Men", "All-New X-Men - Chapter 0008.cbz",
            "<ComicInfo><Series>All-New X-Men</Series><Number>8</Number><Title>All New X-Men v2 008 (2016)</Title></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));

        var archive = Directory.GetFiles(Path.Join(_root, "All-New X-Men")).Single();
        Assert.Equal("All-New X-Men Vol. 2 - Chapter 0008.cbz", Path.GetFileName(archive));
        Assert.Null(ReadComicInfo(archive).Element("Volume"));
    }

    [Fact]
    public async Task Normalize_StoryTitleNamingAVolume_IsNotReadAsOne()
    {
        _settings.WatcherEnableRename = false;
        var path = WriteArchive("X-Men", "X-Men - Chapter 0008.cbz",
            "<ComicInfo><Series>X-Men</Series><Number>8</Number><Title>Vol. 2: The Return (2016)</Title></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));

        var info = ReadComicInfo(path);
        Assert.Null(info.Element("Volume"));
        Assert.Null(info.Element("Year"));
    }

    [Theory]
    // Filled only when unset; a set year is kept even when the name says otherwise.
    [InlineData("", "2012")]
    [InlineData("<Year>2013</Year>", "2013")]
    // 1970, the "date unknown" year some downloaders write, counts as unset.
    [InlineData("<Year>1970</Year>", "2012")]
    public async Task Normalize_CoverYearInTheFileName_FillsOnlyAnEmptyYear(string yearElement, string expected)
    {
        _settings.WatcherEnableRename = false;
        var path = WriteArchive("Saga", "Saga 001 (2012).cbz",
            $"<ComicInfo><Series>Saga</Series><Number>1</Number><Title>Chapter 1</Title>{yearElement}</ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));

        Assert.Equal(expected, ReadComicInfo(path).Element("Year")?.Value);
    }

    [Fact]
    public async Task Normalize_NoVolumeToFill_LeavesVolumeOut()
    {
        // The year after the issue number is the cover date, not a volume.
        _settings.WatcherEnableRename = false;
        var path = WriteArchive("Saga", "Saga 001 (2012).cbz",
            "<ComicInfo><Series>Saga</Series><Number>1</Number></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));

        var info = ReadComicInfo(path);
        Assert.Equal("Chapter 1", info.Element("Title")?.Value);
        Assert.Null(info.Element("Volume"));
    }

    [Theory]
    // Tagged the ComicVine way, written as an issue label, or only named by the file.
    [InlineData("<ComicInfo><Series>Green Lantern Annual</Series><Number>1</Number></ComicInfo>", "a.cbz")]
    [InlineData("<ComicInfo><Series>Green Lantern</Series><Number>Annual 1</Number></ComicInfo>", "b.cbz")]
    [InlineData(null, "Green Lantern Annual 001 (1992).cbz")]
    public async Task ProcessFileAsync_AnnualAndIssueOfTheSameNumber_AreKeptApart(string? annualInfo, string annualFileName)
    {
        var issue = WriteArchive("Green Lantern", "GL 001.cbz", "<ComicInfo><Series>Green Lantern</Series><Number>1</Number></ComicInfo>");
        var annual = WriteArchive("Green Lantern", annualFileName, annualInfo);

        Assert.True(await _service.ProcessFileAsync(issue));
        Assert.True(await _service.ProcessFileAsync(annual));

        var folder = Path.Join(_root, "Green Lantern");
        Assert.True(File.Exists(Path.Join(folder, "Green Lantern - Chapter 0001.cbz")));
        var annualInfoAfter = ReadComicInfo(Path.Join(folder, "Green Lantern Annual - Chapter 0001.cbz"));
        Assert.Equal("Green Lantern Annual", annualInfoAfter.Element("Series")?.Value);
        Assert.Equal("1", annualInfoAfter.Element("Number")?.Value);
        _fileStore.Verify(
            f => f.MarkFileDuplicateAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessFileAsync_AnnualNamedOnlyByItsTitle_MovesOffTheIssuesName()
    {
        // Renamed before Annuals were told apart: only the title left by the tagger still says
        // "Annual", and the file holds the name of issue #1.
        var path = WriteArchive("Batman (2016-)", "Batman - Chapter 0001.cbz",
            "<ComicInfo><Series>Batman</Series><Title>#- Annual 01</Title><Number>1</Number></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path, forceReprocess: true));

        var archive = Directory.GetFiles(Path.Join(_root, "Batman (2016-)")).Single();
        Assert.Equal("Batman Annual (2016) - Chapter 0001.cbz", Path.GetFileName(archive));
        Assert.Equal("Annual", ReadComicInfo(archive).Element("Format")?.Value);
    }

    [Fact]
    public async Task ProcessFileAsync_AOneShotAndIssue1_AreKeptApart_AndTheOneShotIsMarkedInFormat()
    {
        var issue1 = WriteArchive("Batman", "Batman 001.cbz", "<ComicInfo><Series>Batman</Series><Number>1</Number></ComicInfo>");
        var oneShot = WriteArchive("Batman", "Batman (One-Shot).cbz", "<ComicInfo><Series>Batman</Series><Number>1</Number></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(issue1));
        Assert.True(await _service.ProcessFileAsync(oneShot));

        var folder = Path.Join(_root, "Batman");
        Assert.True(File.Exists(Path.Join(folder, "Batman - Chapter 0001.cbz")));
        var renamed = Path.Join(folder, "Batman One-Shot - Chapter 0001.cbz");
        Assert.True(File.Exists(renamed), "the One-Shot is named apart from issue #1");
        Assert.Equal("One-Shot", ReadComicInfo(renamed).Element("Format")?.Value);
        _fileStore.Verify(f => f.MarkFileDuplicateAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessFileAsync_AFormatTheUserNamed_IsAKindOfItsOwn()
    {
        // DC's Batman: Rebirth one-shot, marked Rebirth by the user.
        var issue1 = WriteArchive("Batman", "Batman 001.cbz", "<ComicInfo><Series>Batman</Series><Number>1</Number></ComicInfo>");
        var rebirth = WriteArchive("Batman", "Batman Rebirth 001.cbz", "<ComicInfo><Series>Batman Rebirth</Series><Number>1</Number><Format>Rebirth</Format></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(issue1));
        Assert.True(await _service.ProcessFileAsync(rebirth));

        var folder = Path.Join(_root, "Batman");
        Assert.True(File.Exists(Path.Join(folder, "Batman - Chapter 0001.cbz")));
        Assert.True(File.Exists(Path.Join(folder, "Batman Rebirth - Chapter 0001.cbz")), "named once, apart from issue #1");
        _fileStore.Verify(f => f.MarkFileDuplicateAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessFileAsync_AnAnnualNamedByItsYear_KeepsNoIssueNumber()
    {
        // "#Annual 2026" names the 2026 Annual: 2026 is its year, not its issue number.
        var path = WriteArchive("Absolute Wonder Woman", "#Annual 2026.cbz",
            "<ComicInfo><Series>Absolute Wonder Woman Annual</Series><Title>#Annual 2026</Title><Year>2026</Year><Format>Annual</Format></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));

        var renamed = Directory.GetFiles(Path.Join(_root, "Absolute Wonder Woman")).Single();
        var info = ReadComicInfo(renamed);
        Assert.Null(info.Element("Number"));
        Assert.Equal("#Annual 2026", info.Element("Title")?.Value);
        // The title stands in for the number, without saying "Annual" twice.
        Assert.Equal("Absolute Wonder Woman Annual - 2026.cbz", Path.GetFileName(renamed));
    }

    [Fact]
    public async Task ProcessFileAsync_AnAnnualWhoseNumberTheUserCleared_StaysWithoutOne()
    {
        // Renamed by its old number before the user cleared it; the name must not bring it back.
        var path = WriteArchive("Absolute Wonder Woman", "Absolute Wonder Woman Annual - Chapter 0021.cbz",
            "<ComicInfo><Series>Absolute Wonder Woman Annual</Series><Year>2026</Year><Format>Annual</Format></ComicInfo>");
        _fileStore.Setup(f => f.GetFileAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(new ComicFile
        {
            FilePath = path,
            Metadata = new ComicMetadata { Series = "Absolute Wonder Woman Annual", IsUserEdited = true, UserLockedFieldsMask = (long)ComicMetadataFieldFlags.Issue }
        });

        Assert.True(await _service.ProcessFileAsync(path, forceReprocess: true));

        var renamed = Directory.GetFiles(Path.Join(_root, "Absolute Wonder Woman")).Single();
        var info = ReadComicInfo(renamed);
        Assert.Null(info.Element("Number"));
        Assert.Equal("Annual 2026", info.Element("Title")?.Value);
        Assert.Equal("Absolute Wonder Woman Annual - 2026.cbz", Path.GetFileName(renamed));
    }

    [Fact]
    public async Task ProcessFileAsync_AnAnnualWhoseLockedNumberWentMissing_TakesItBackFromItsName()
    {
        // The user set #21 and it was locked; a tagger since dropped <Number>. That is not clearing it.
        var path = WriteArchive("Absolute Wonder Woman", "Absolute Wonder Woman Annual - Chapter 0021.cbz",
            "<ComicInfo><Series>Absolute Wonder Woman Annual</Series><Year>2026</Year><Format>Annual</Format></ComicInfo>");
        _fileStore.Setup(f => f.GetFileAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(new ComicFile
        {
            FilePath = path,
            Metadata = new ComicMetadata { Issue = "21", IsUserEdited = true, UserLockedFieldsMask = (long)ComicMetadataFieldFlags.Issue }
        });
        _settings.WatcherEnableRename = false;

        Assert.True(await _service.ProcessFileAsync(path, forceReprocess: true));

        Assert.Equal("21", ReadComicInfo(path).Element("Number")?.Value);
    }

    [Fact]
    public async Task ProcessFileAsync_TwoAnnualsWithoutANumberOfOneName_KeepTheirNames_NotFlaggedAsDuplicates()
    {
        var first = WriteArchive("Saga", "Saga Annual (2026) a.cbz", "<ComicInfo><Series>Saga Annual</Series><Format>Annual</Format><Year>2026</Year></ComicInfo>");
        var second = WriteArchive("Saga", "Saga Annual (2026) b.cbz", "<ComicInfo><Series>Saga Annual</Series><Format>Annual</Format><Year>2026</Year></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(first));
        Assert.True(await _service.ProcessFileAsync(second));

        Assert.Equal(new[] { "Saga Annual (2026) b.cbz", "Saga Annual - 2026.cbz" },
            Directory.GetFiles(Path.Join(_root, "Saga")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        _fileStore.Verify(f => f.MarkFileDuplicateAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Normalize_AnAnnualWithoutANumber_TakesNoCoverYearFromABracketedYear()
    {
        // With no issue number before it, "(2019)" may be the volume's start year: not its cover year.
        _settings.WatcherEnableRename = false;
        var path = WriteArchive("Saga", "Saga Annual (2019).cbz", "<ComicInfo><Series>Saga Annual</Series><Format>Annual</Format></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));

        var info = ReadComicInfo(path);
        Assert.Equal(("Annual", default(string?), default(string?)), (info.Element("Title")?.Value, info.Element("Year")?.Value, info.Element("Number")?.Value));
    }

    [Fact]
    public async Task UpdateMetadataJob_LocksOnlyTheIssueNumberItWrites()
    {
        var path = WriteArchive("Saga", "Saga - Chapter 0001.cbz", "<ComicInfo><Series>Saga</Series><Number>1</Number></ComicInfo>");
        var registry = TestServices.CreateBatchJobRegistry(new TestOptionsMonitor<AppSettings>(_settings), NullLogger<BatchJobRegistry>.Instance);
        using var service = TestServices.CreateComicProcessor(
            new TestOptionsMonitor<AppSettings>(_settings), NullLogger<ComicProcessorService>.Instance, _fileStore.Object,
            Mock.Of<IProcessingHistoryService>(), jobRegistry: registry);

        var jobId = await service.UpdateMetadataAsync(new[] { path }, new ComicMetadata { Series = "Saga Deluxe", Issue = "2" },
            ComicMetadataFieldFlags.Series | ComicMetadataFieldFlags.Issue);
        await registry.WaitForCompletionAsync(jobId);

        Assert.Equal(("Saga Deluxe", "2"), (ReadComicInfo(path).Element("Series")?.Value, ReadComicInfo(path).Element("Number")?.Value));
        _fileStore.Verify(f => f.RecordUserMetadataEditAsync(path, It.IsAny<ComicMetadata>(), ComicMetadataFieldFlags.Issue, false, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.RecordUserMetadataEditAsync(It.IsAny<string>(), It.IsAny<ComicMetadata>(), It.Is<ComicMetadataFieldFlags>(fl => fl.HasFlag(ComicMetadataFieldFlags.Series)), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessFileAsync_AnAnnualWithoutANumberTheUserLeftAlone_TakesTheNumberItsNameGives()
    {
        var path = WriteArchive("Batman", "Batman Annual 003.cbz",
            "<ComicInfo><Series>Batman Annual</Series><Format>Annual</Format></ComicInfo>");
        _settings.WatcherEnableRename = false;

        Assert.True(await _service.ProcessFileAsync(path));

        Assert.Equal("3", ReadComicInfo(path).Element("Number")?.Value);
    }

    [Fact]
    public async Task Normalize_AnAnnualWithoutANumberOrYear_IsTitledAnnual_AndIsThenNormalized()
    {
        _settings.WatcherEnableRename = false;
        var path = WriteArchive("Saga", "Saga Annual.cbz",
            "<ComicInfo><Series>Saga Annual</Series><Format>Annual</Format><Title>Chapter Unknown</Title></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));
        var written = File.GetLastWriteTimeUtc(path);
        Assert.True(await _service.ProcessFileAsync(path, forceReprocess: true));

        var info = ReadComicInfo(path);
        Assert.Equal(("Annual", default(string?)), (info.Element("Title")?.Value, info.Element("Number")?.Value));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_LocksTheFieldsTheUserWrote()
    {
        var path = WriteArchive("Absolute Wonder Woman", "Absolute Wonder Woman Annual - Chapter 0021.cbz",
            "<ComicInfo><Series>Absolute Wonder Woman Annual</Series><Number>21</Number><Format>Annual</Format></ComicInfo>");

        var result = await _service.ApplyMetadataEditAsync(path, new ComicMetadata { Title = "The 2026 Annual" }, ComicMetadataFieldFlags.Issue | ComicMetadataFieldFlags.Title);

        Assert.True(result.Succeeded);
        Assert.Null(ReadComicInfo(path).Element("Number"));
        // Only the issue number is locked: the title stays free to follow ComicVine.
        _fileStore.Verify(f => f.RecordUserMetadataEditAsync(path, It.IsAny<ComicMetadata>(), ComicMetadataFieldFlags.Issue, false, It.IsAny<CancellationToken>()), Times.Once);

        // Saving what the archive already holds is the user's decision too.
        await _service.ApplyMetadataEditAsync(path, new ComicMetadata(), ComicMetadataFieldFlags.Issue);
        _fileStore.Verify(f => f.RecordUserMetadataEditAsync(path, It.IsAny<ComicMetadata>(), ComicMetadataFieldFlags.Issue, false, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ProcessFileAsync_AnnualOfAVolume_KeepsAnnualBeforeTheVolume()
    {
        var path = WriteArchive("Green Lantern (1990)", "Green Lantern (1990) Annual 003.cbz");

        Assert.True(await _service.ProcessFileAsync(path));

        var archive = Directory.GetFiles(Path.Join(_root, "Green Lantern (1990)")).Single();
        Assert.Equal("Green Lantern Annual (1990) - Chapter 0003.cbz", Path.GetFileName(archive));
        var info = ReadComicInfo(archive);
        Assert.Equal("Green Lantern Annual (1990)", info.Element("Series")?.Value);
        Assert.Null(info.Element("Volume"));
        _fileStore.Verify(f => f.SetVolumeYearAsync(path, 1990, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Normalize_AnnualWithAnEmptyFormat_IsMarkedInFormat()
    {
        _settings.WatcherEnableRename = false;
        var path = WriteArchive("Green Lantern", "Green Lantern Annual - Chapter 0001.cbz",
            "<ComicInfo><Series>Green Lantern Annual</Series><Number>1</Number><Title>Chapter 1</Title></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));

        Assert.Equal("Annual", ReadComicInfo(path).Element("Format")?.Value);
    }

    [Theory]
    [InlineData("<Format>TPB</Format>", "TPB")]
    [InlineData("", null)]
    public async Task Normalize_KeepsASetFormat_AndMarksNoRegularIssue(string formatElement, string? expected)
    {
        _settings.WatcherEnableRename = false;
        var series = formatElement.Length == 0 ? "Green Lantern" : "Green Lantern Annual";
        var path = WriteArchive("Green Lantern", "x.cbz",
            $"<ComicInfo><Series>{series}</Series><Number>1</Number><Title>Chapter 1</Title>{formatElement}</ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path));

        Assert.Equal(expected, ReadComicInfo(Directory.GetFiles(Path.Join(_root, "Green Lantern")).Single()).Element("Format")?.Value);
    }

    [Fact]
    public async Task ProcessFileAsync_SpecialAndIssueOfTheSameNumber_AreKeptApart()
    {
        var issue = WriteArchive("Green Lantern", "a.cbz", "<ComicInfo><Series>Green Lantern</Series><Number>1</Number></ComicInfo>");
        var special = WriteArchive("Green Lantern", "b.cbz", "<ComicInfo><Series>Green Lantern</Series><Number>1</Number><Format>Special</Format></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(issue));
        Assert.True(await _service.ProcessFileAsync(special));

        var folder = Path.Join(_root, "Green Lantern");
        Assert.True(File.Exists(Path.Join(folder, "Green Lantern - Chapter 0001.cbz")));
        var info = ReadComicInfo(Path.Join(folder, "Green Lantern Special - Chapter 0001.cbz"));
        Assert.Equal("Green Lantern", info.Element("Series")?.Value);
        Assert.Equal("Special", info.Element("Format")?.Value);
        _fileStore.Verify(
            f => f.MarkFileDuplicateAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("Giant 2025")]
    [InlineData("#Giant 2025 2")]
    public async Task ProcessFileAsync_GiantSizeNamedOnlyByItsTitle_IsMarkedAndNamedAsOne(string title)
    {
        // Tagged as regular issue #2025: only the title left by the tagger says "Giant".
        var path = WriteArchive("X-Men", "X-Men - Chapter 2025.cbz",
            $"<ComicInfo><Series>X-Men</Series><Title>{title}</Title><Number>2025</Number></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path, forceReprocess: true));

        var archive = Directory.GetFiles(Path.Join(_root, "X-Men")).Single();
        Assert.Equal("X-Men Giant-Size - Chapter 2025.cbz", Path.GetFileName(archive));
        var info = ReadComicInfo(archive);
        Assert.Equal("Giant-Size", info.Element("Format")?.Value);
        Assert.Equal("2025", info.Element("Number")?.Value);
    }

    [Fact]
    public async Task ProcessFileAsync_SpecialWithNoNumber_IsNamedByItsTitle()
    {
        // Renamed before by an older version, to a bare "- Chapter".
        var path = WriteArchive("All-New X-Men", "All-New X-Men - Chapter.cbz",
            "<ComicInfo><Series>All-New X-Men</Series><Title>All New X-Men .Special – One Down</Title></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path, forceReprocess: true));

        var archive = Directory.GetFiles(Path.Join(_root, "All-New X-Men")).Single();
        Assert.Equal("All-New X-Men Special - One Down.cbz", Path.GetFileName(archive));
        var info = ReadComicInfo(archive);
        Assert.Equal("Special", info.Element("Format")?.Value);
        Assert.Equal("All New X-Men .Special – One Down", info.Element("Title")?.Value);
        Assert.Null(info.Element("Number"));
    }

    [Fact]
    public async Task ProcessFileAsync_BareChapterNameWithNoNumberOrTitle_LosesTheBareChapter()
    {
        var path = WriteArchive("Her Summon", "Her Summon - Chapter.cbz",
            "<ComicInfo><Series>Her Summon</Series></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path, forceReprocess: true));

        var archive = Directory.GetFiles(Path.Join(_root, "Her Summon")).Single();
        Assert.Equal("Her Summon.cbz", Path.GetFileName(archive));
    }

    [Fact]
    public async Task ProcessFileAsync_NoNumberOrTitle_KeepsTheName()
    {
        // The format would only give "Her Summon", which every such issue would share.
        var path = WriteArchive("Her Summon", "Her Summon Extra.cbz",
            "<ComicInfo><Series>Her Summon</Series></ComicInfo>");

        Assert.True(await _service.ProcessFileAsync(path, forceReprocess: true));

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_VolumeYearOnly_IsStoredAndLeavesTheArchiveAlone()
    {
        var path = WriteArchive("Saga", "Saga 001.cbz", "<ComicInfo><Series>Saga</Series><Number>1</Number></ComicInfo>");
        var before = File.ReadAllBytes(path);

        var result = await _service.ApplyMetadataEditAsync(path, new ComicMetadata { VolumeYear = 2012 }, ComicMetadataFieldFlags.VolumeYear);

        Assert.Equal(MetadataWriteOutcome.Unchanged, result.Outcome);
        Assert.Equal(before, File.ReadAllBytes(path));
        _fileStore.Verify(f => f.SetVolumeYearAsync(path, 2012, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_VolumeYearWithOtherFields_StoresItAndWritesTheRest()
    {
        var path = WriteArchive("Saga", "Saga 001.cbz", "<ComicInfo><Series>Saga</Series><Number>1</Number></ComicInfo>");

        await _service.ApplyMetadataEditAsync(
            path,
            new ComicMetadata { Volume = "2", VolumeYear = 2012 },
            ComicMetadataFieldFlags.Volume | ComicMetadataFieldFlags.VolumeYear);

        var info = ReadComicInfo(path);
        Assert.Equal("2", info.Element("Volume")?.Value);
        Assert.DoesNotContain("2012", info.ToString());
        _fileStore.Verify(f => f.SetVolumeYearAsync(path, 2012, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static string VolumeInfo(string volume, string number)
        => $"<ComicInfo><Series>Green Lantern</Series><Number>{number}</Number><Volume>{volume}</Volume></ComicInfo>";

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
