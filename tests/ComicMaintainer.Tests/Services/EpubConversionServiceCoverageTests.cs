using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using ImageMagick;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers argument validation, the size-budget compression tiers, validation of
/// the written book and the image/metadata edge cases of
/// <see cref="EpubConversionService"/>.
/// </summary>
public class EpubConversionServiceCoverageTests : IDisposable
{
    private readonly string _workDir;
    private readonly CapturingLogger _logger = new();
    private readonly EpubConversionService _service;

    public EpubConversionServiceCoverageTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"epub-cov-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _service = new EpubConversionService(_logger);
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithOnlyACancellationToken_ConvertsTheFile()
    {
        var cbz = CreateCbz("Token Overload.cbz", Page("001.jpg"));
        var outputDir = Path.Combine(_workDir, "token");

        var epubPath = await _service.ConvertToEpubAsync(cbz, outputDir, CancellationToken.None);

        Assert.Equal(Path.Combine(outputDir, "Token Overload.epub"), epubPath);
        Assert.True(File.Exists(epubPath));
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithABlankComicPath_Throws()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _service.ConvertToEpubAsync("   ", Path.Combine(_workDir, "out")));

        Assert.Equal("comicFilePath", ex.ParamName);
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithABlankOutputDirectory_Throws()
    {
        var cbz = CreateCbz("Blank Output.cbz", Page("001.jpg"));

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _service.ConvertToEpubAsync(new[] { cbz }, "  "));

        Assert.Equal("outputDirectory", ex.ParamName);
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithABlankPathInTheList_ThrowsBeforeWritingAnything()
    {
        var cbz = CreateCbz("Blank Entry List.cbz", Page("001.jpg"));
        var outputDir = Path.Combine(_workDir, "blank-entry");

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _service.ConvertToEpubAsync(new[] { cbz, " " }, outputDir));

        Assert.Equal("comicFilePaths", ex.ParamName);
        Assert.False(Directory.Exists(outputDir));
    }

    [Theory]
    [InlineData(0x00FF)] // A compression method the archive reader does not implement.
    [InlineData(0x0008)] // Stored bytes that are not a valid deflate stream.
    public async Task ConvertToEpubAsync_WhenAnEmbeddedCoverCannotBeRead_KeepsItAsAPageAndFails(int method)
    {
        var seriesImage = Path.Combine(_workDir, "series.jpg");
        File.WriteAllBytes(seriesImage, CreateImage(MagickFormat.Jpeg, 12, 18));
        var cbz = CreateCbz("Unreadable Cover.cbz", Page("001.jpg"), Page("002.jpg"), Page("cover.jpg"));
        SetCompressionMethod(cbz, "cover.jpg", method: (ushort)method);
        var outputDir = Path.Join(_workDir, $"unreadable-cover-{method}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ConvertToEpubAsync(
            new[] { cbz },
            outputDir,
            new EpubConversionOptions(SeriesImagePath: seriesImage)));

        // The unreadable entry is not mistaken for the series cover and dropped:
        // it stays a page, and reading it fails the conversion naming it.
        Assert.StartsWith("Page 'cover.jpg' in Unreadable Cover.cbz could not be read: ", ex.Message);
        Assert.Contains(_logger.Entries, e =>
            e.Level == LogLevel.Debug &&
            e.Exception is not null &&
            e.Message.Contains("'cover.jpg'", StringComparison.Ordinal) &&
            e.Message.Contains("while looking for an embedded series cover", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(outputDir));
    }

    [Fact]
    public async Task ConvertToEpubAsync_WhenTheRequestedNameSanitizesToNothing_UsesTheSourceName()
    {
        var cbz = CreateCbz("Source Name.cbz", Page("001.jpg"));

        var epubPath = await _service.ConvertToEpubAsync(
            new[] { cbz },
            Path.Combine(_workDir, "dots"),
            new EpubConversionOptions(OutputFileName: " . "));

        Assert.Equal("Source Name.epub", Path.GetFileName(epubPath));
    }

    [Fact]
    public async Task ConvertToEpubAsync_TruncatesALongRequestedNameAndTrimsTheCut()
    {
        var cbz = CreateCbz("Long Name.cbz", Page("001.jpg"));
        var requested = new string('a', 119) + " " + new string('b', 20);

        var epubPath = await _service.ConvertToEpubAsync(
            new[] { cbz },
            Path.Combine(_workDir, "long-name"),
            new EpubConversionOptions(OutputFileName: requested));

        Assert.Equal(new string('a', 119) + ".epub", Path.GetFileName(epubPath));
        Assert.True(File.Exists(epubPath));
    }

    [Fact]
    public async Task ConvertToEpubAsync_WhenAPageDisappearsAfterIndexing_FailsNamingThePage()
    {
        var first = CreateCbz("Vanishing 1.cbz", Page("001.jpg"), Page("002.jpg"));
        var second = CreateCbz("Vanishing 2.cbz", Page("001.jpg"));
        var outputDir = Path.Combine(_workDir, "vanishing");
        var rewritten = false;
        var progress = new SyncProgress(report =>
        {
            // The second issue is about to be indexed: the first one has been
            // closed again and its page list is already fixed.
            if (report.Phase == EpubConversionPhase.Reading && report.CompletedIssues == 1 && !rewritten)
            {
                CreateCbz("Vanishing 1.cbz", Page("002.jpg"));
                rewritten = true;
            }
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ConvertToEpubAsync(
            new[] { first, second },
            outputDir,
            new EpubConversionOptions(Progress: progress)));

        Assert.True(rewritten);
        Assert.Equal("Page '001.jpg' is no longer present in Vanishing 1.cbz.", ex.Message);
        Assert.Empty(Directory.GetFiles(outputDir));
    }

    [Theory]
    [InlineData(0x00FF)] // A compression method the archive reader does not implement.
    [InlineData(0x0008)] // Stored bytes that are not a valid deflate stream.
    public async Task ConvertToEpubAsync_WhenAPageCannotBeRead_FailsNamingThePageAndTheComic(int method)
    {
        var cbz = CreateCbz("Unreadable Page.cbz", Page("001.jpg"), Page("002.jpg"));
        SetCompressionMethod(cbz, "002.jpg", (ushort)method);
        var outputDir = Path.Join(_workDir, $"unreadable-page-{method}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ConvertToEpubAsync(new[] { cbz }, outputDir));

        // The archive reader's own explanation is kept, after the page and comic it is about.
        Assert.NotNull(ex.InnerException);
        Assert.Equal(
            $"Page '002.jpg' in Unreadable Page.cbz could not be read: {ex.InnerException.Message}",
            ex.Message);
        Assert.Empty(Directory.GetFiles(outputDir));
    }

    [Fact]
    public async Task ConvertToEpubAsync_CondensedBook_WhenAPageCannotBeDecoded_NamesItsIssue()
    {
        var first = CreateCbz("Condensed 1.cbz", Page("001.jpg"), Page("002.jpg"));
        var second = CreateCbz("Condensed 2.cbz", Page("001.jpg"), ("002.jpg", Encoding.ASCII.GetBytes("not an image")));
        var outputDir = Path.Join(_workDir, "condensed-undecodable");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ConvertToEpubAsync(
            new[] { first, second },
            outputDir,
            new EpubConversionOptions(OutputFileName: "Condensed 1-2")));

        // Every issue has a '002.jpg': only the comic name says which one to fix.
        Assert.Equal(
            "Page image '002.jpg' in Condensed 2.cbz could not be decoded and would render as a blank page.",
            ex.Message);
        Assert.Empty(Directory.GetFiles(outputDir));
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithACbr_ReadsPagesAndMetadataFromTheRarArchive()
    {
        var cbr = Path.Combine(_workDir, "Rar Issue.cbr");
        ComicReaderServiceCoverageTests.StoredRar4.Write(
            cbr,
            ("ComicInfo.xml", Encoding.UTF8.GetBytes("<ComicInfo><Series>Rar Series</Series><Number>4</Number></ComicInfo>")),
            ("002.jpg", CreateImage(MagickFormat.Jpeg, 10, 14)),
            ("001.jpg", CreateImage(MagickFormat.Jpeg, 12, 16)));

        var epubPath = await _service.ConvertToEpubAsync(new[] { cbr }, Path.Combine(_workDir, "rar"));

        Assert.Equal("Rar Issue.epub", Path.GetFileName(epubPath));
        using var archive = ZipFile.OpenRead(epubPath);
        Assert.Contains("""<dc:title id="title">Rar Series #004</dc:title>""", ReadEntry(archive, "OEBPS/content.opf"));
        Assert.Contains("width=12, height=16", ReadEntry(archive, "OEBPS/page0001.xhtml"));
        Assert.Contains("width=10, height=14", ReadEntry(archive, "OEBPS/page0002.xhtml"));
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithMalformedComicInfo_WarnsAndTitlesTheBookAfterTheFile()
    {
        var cbz = CreateCbz(
            "Malformed Info.cbz",
            ("ComicInfo.xml", Encoding.UTF8.GetBytes("<ComicInfo><Series>Broken")),
            Page("001.jpg"));

        var epubPath = await _service.ConvertToEpubAsync(new[] { cbz }, Path.Combine(_workDir, "malformed"));

        using var archive = ZipFile.OpenRead(epubPath);
        Assert.Contains("""<dc:title id="title">Malformed Info</dc:title>""", ReadEntry(archive, "OEBPS/content.opf"));
        Assert.Contains(_logger.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Exception is InvalidOperationException &&
            e.Message.Contains("Failed to read ComicInfo.xml", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("<Title>  Standalone Story  </Title>", "Standalone Story")]
    [InlineData("<Series>Hash</Series><Number>#</Number>", "Hash ##")]
    [InlineData("<Series>Dots</Series><Number>1.5.2</Number>", "Dots #001.5")]
    [InlineData("<Series>Dots</Series><Number>12.</Number>", "Dots #012")]
    [InlineData("<Series>Dots</Series><Number>3.x</Number>", "Dots #003")]
    [InlineData("<Series>Huge</Series><Number>123456789012345678901234567890123</Number>", "Huge #123456789012345678901234567890123")]
    public async Task ConvertToEpubAsync_TitlesTheBookFromTheComicInfo(string fields, string expectedTitle)
    {
        var cbz = CreateCbz(
            "Titled.cbz",
            ("ComicInfo.xml", Encoding.UTF8.GetBytes($"<ComicInfo>{fields}</ComicInfo>")),
            Page("001.jpg"));

        var epubPath = await _service.ConvertToEpubAsync(new[] { cbz }, Path.Combine(_workDir, "titled"));

        using var archive = ZipFile.OpenRead(epubPath);
        var opf = ReadEntry(archive, "OEBPS/content.opf");
        Assert.Contains($"""<dc:title id="title">{expectedTitle}</dc:title>""", opf);
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithAnUnparsableIssueNumber_OmitsTheSeriesPosition()
    {
        var cbz = CreateCbz(
            "Huge Number.cbz",
            ("ComicInfo.xml", Encoding.UTF8.GetBytes("<ComicInfo><Series>Huge</Series><Number>123456789012345678901234567890123</Number></ComicInfo>")),
            Page("001.jpg"));

        var epubPath = await _service.ConvertToEpubAsync(new[] { cbz }, Path.Combine(_workDir, "huge"));

        using var archive = ZipFile.OpenRead(epubPath);
        var opf = ReadEntry(archive, "OEBPS/content.opf");
        Assert.Contains("""<meta name="calibre:series" content="Huge"/>""", opf);
        Assert.DoesNotContain("group-position", opf);
        Assert.DoesNotContain("calibre:series_index", opf);
    }

    [Theory]
    [InlineData("fr", "fr")]
    [InlineData(" pt-BR ", "pt-BR")]
    [InlineData("en_US!", "en")]
    public async Task ConvertToEpubAsync_WritesTheLanguagePublisherAndSummary(string language, string expectedLanguage)
    {
        var cbz = CreateCbz(
            "Described.cbz",
            ("ComicInfo.xml", Encoding.UTF8.GetBytes($"""
                <ComicInfo>
                  <Series>Described</Series>
                  <Number>2</Number>
                  <Publisher>  Image &amp; Co  </Publisher>
                  <Summary> A &lt;b&gt;bold&lt;/b&gt; tale </Summary>
                  <LanguageISO>{language}</LanguageISO>
                </ComicInfo>
                """)),
            Page("001.jpg"));

        var epubPath = await _service.ConvertToEpubAsync(new[] { cbz }, Path.Combine(_workDir, "described"));

        using var archive = ZipFile.OpenRead(epubPath);
        var opf = ReadEntry(archive, "OEBPS/content.opf");
        Assert.Contains($"<dc:language>{expectedLanguage}</dc:language>", opf);
        Assert.Contains("<dc:publisher>Image &amp; Co</dc:publisher>", opf);
        Assert.Contains("<dc:description>A &lt;b&gt;bold&lt;/b&gt; tale</dc:description>", opf);
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithAnEmptySeriesImage_OpensWithTheFirstPage()
    {
        var seriesImage = Path.Combine(_workDir, "empty-series.jpg");
        File.WriteAllBytes(seriesImage, Array.Empty<byte>());
        var cbz = CreateCbz("Empty Series Image.cbz", Page("001.jpg"));

        var epubPath = await _service.ConvertToEpubAsync(
            new[] { cbz },
            Path.Combine(_workDir, "empty-series"),
            new EpubConversionOptions(SeriesImagePath: seriesImage));

        using var archive = ZipFile.OpenRead(epubPath);
        Assert.Null(archive.GetEntry("OEBPS/cover.xhtml"));
        Assert.Contains(
            """<item id="cover-image" href="images/page0001.jpg" media-type="image/jpeg" properties="cover-image"/>""",
            ReadEntry(archive, "OEBPS/content.opf"));
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithASeriesImageBeyondTheDecodeLimit_OpensWithTheFirstPage()
    {
        var seriesImage = Path.Combine(_workDir, "bomb.png");
        File.WriteAllBytes(seriesImage, CreatePngDeclaring(12000, 12000));
        var cbz = CreateCbz("Bomb Cover.cbz", Page("001.jpg"));

        var epubPath = await _service.ConvertToEpubAsync(
            new[] { cbz },
            Path.Combine(_workDir, "bomb-cover"),
            new EpubConversionOptions(SeriesImagePath: seriesImage));

        using var archive = ZipFile.OpenRead(epubPath);
        Assert.Null(archive.GetEntry("OEBPS/cover.xhtml"));
        Assert.Contains(_logger.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("(12000x12000) exceed the decode limit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithAPageBeyondTheDecodeLimit_Fails()
    {
        var cbz = CreateCbz("Bomb Page.cbz", ("001.png", CreatePngDeclaring(12000, 12000)));
        var outputDir = Path.Combine(_workDir, "bomb-page");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ConvertToEpubAsync(new[] { cbz }, outputDir));

        Assert.Equal("Page image '001.png' in Bomb Page.cbz has dimensions (12000x12000) that exceed the decode limit.", ex.Message);
        Assert.Empty(Directory.GetFiles(outputDir));
    }

    [Theory]
    [InlineData(MagickFormat.Bmp, "series.bmp")]
    [InlineData(MagickFormat.WebP, "series.webp")]
    public async Task ConvertToEpubAsync_ReencodesASeriesImageEreadersCannotShowAsJpeg(MagickFormat format, string fileName)
    {
        var seriesImage = Path.Combine(_workDir, fileName);
        File.WriteAllBytes(seriesImage, CreateImage(format, 16, 24));
        var cbz = CreateCbz("Reencoded Cover.cbz", Page("001.jpg"));

        var epubPath = await _service.ConvertToEpubAsync(
            new[] { cbz },
            Path.Combine(_workDir, "reencoded-" + format),
            new EpubConversionOptions(SeriesImagePath: seriesImage));

        using var archive = ZipFile.OpenRead(epubPath);
        Assert.Contains(
            """<item id="cover-image" href="images/cover.jpg" media-type="image/jpeg" properties="cover-image"/>""",
            ReadEntry(archive, "OEBPS/content.opf"));
        var cover = new MagickImageInfo(ReadBytes(archive, "OEBPS/images/cover.jpg"));
        Assert.Equal(MagickFormat.Jpeg, cover.Format);
        Assert.Equal(16u, cover.Width);
        Assert.Equal(24u, cover.Height);
        Assert.Contains("width=16, height=24", ReadEntry(archive, "OEBPS/cover.xhtml"));
    }

    [Fact]
    public async Task ConvertToEpubAsync_KeepsGifPagesAndReencodesOtherFormatsAsJpeg()
    {
        var gif = CreateImage(MagickFormat.Gif, 10, 12);
        var cbz = CreateCbz(
            "Formats.cbz",
            ("001.gif", gif),
            ("002.bmp", CreateImage(MagickFormat.Bmp, 10, 12)),
            // Archive names routinely lie about the format.
            ("003.jpg", CreateImage(MagickFormat.Tiff, 10, 12)));

        var epubPath = await _service.ConvertToEpubAsync(new[] { cbz }, Path.Combine(_workDir, "formats"));

        using var archive = ZipFile.OpenRead(epubPath);
        var opf = ReadEntry(archive, "OEBPS/content.opf");
        Assert.Contains("""href="images/page0001.gif" media-type="image/gif" """.TrimEnd(), opf);
        Assert.Contains("""href="images/page0002.jpg" media-type="image/jpeg" """.TrimEnd(), opf);
        Assert.Contains("""href="images/page0003.jpg" media-type="image/jpeg" """.TrimEnd(), opf);
        Assert.Equal(gif, ReadBytes(archive, "OEBPS/images/page0001.gif"));
        Assert.Equal(MagickFormat.Jpeg, new MagickImageInfo(ReadBytes(archive, "OEBPS/images/page0002.jpg")).Format);
        Assert.Equal(MagickFormat.Jpeg, new MagickImageInfo(ReadBytes(archive, "OEBPS/images/page0003.jpg")).Format);
    }

    [Fact]
    public async Task ConvertToEpubAsync_IgnoresEntriesWithBlankNames()
    {
        var cbz = CreateCbz("Blank Name.cbz", (" ", new byte[] { 1, 2, 3 }), Page("001.jpg"));

        var epubPath = await _service.ConvertToEpubAsync(new[] { cbz }, Path.Combine(_workDir, "blank-name"));

        using var archive = ZipFile.OpenRead(epubPath);
        Assert.Single(archive.Entries, e => e.FullName.StartsWith("OEBPS/images/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConvertToEpubAsync_WhenEveryTierIsOverBudget_ShipsTheSmallestVariant()
    {
        var cbz = CreateCbz("Wide.cbz", ("001.jpg", CreateImage(MagickFormat.Jpeg, 3000, 60)));
        var outputDir = Path.Combine(_workDir, "over-budget");
        var reports = new List<EpubConversionProgress>();

        var epubPath = await _service.ConvertToEpubAsync(
            new[] { cbz },
            outputDir,
            new EpubConversionOptions(MaxSizeBytes: 1, Progress: new SyncProgress(reports.Add)));

        // Each tier is tried in turn; the last one is delivered even though it
        // is still over the (unreachable) budget.
        var retries = reports.Where(r => r.Phase == EpubConversionPhase.Recompressing).ToList();
        Assert.Equal(new[] { 2, 3, 4 }, retries.Select(r => r.Pass));
        Assert.All(reports, r => Assert.Equal(4, r.TotalPasses));
        Assert.Equal(4, reports.Count(r => r.Phase == EpubConversionPhase.Validating));
        Assert.Equal(3, _logger.Entries.Count(e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("exceeds the 1 byte budget", StringComparison.Ordinal)));
        Assert.True(new FileInfo(epubPath).Length > 1);
        Assert.Equal(new[] { epubPath }, Directory.GetFiles(outputDir));

        using var archive = ZipFile.OpenRead(epubPath);
        var page = new MagickImageInfo(ReadBytes(archive, "OEBPS/images/page0001.jpg"));
        Assert.Equal(MagickFormat.Jpeg, page.Format);
        Assert.Equal(1400u, page.Width);
        Assert.Equal(28u, page.Height);
        Assert.Contains("width=1400, height=28", ReadEntry(archive, "OEBPS/page0001.xhtml"));
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithAZeroBudget_BuildsASinglePass()
    {
        var cbz = CreateCbz("Zero Budget.cbz", Page("001.jpg"));
        var reports = new List<EpubConversionProgress>();

        var epubPath = await _service.ConvertToEpubAsync(
            new[] { cbz },
            Path.Combine(_workDir, "zero-budget"),
            new EpubConversionOptions(MaxSizeBytes: 0, Progress: new SyncProgress(reports.Add)));

        Assert.True(File.Exists(epubPath));
        Assert.NotEmpty(reports);
        Assert.All(reports, r => Assert.Equal(1, r.TotalPasses));
        Assert.DoesNotContain(reports, r => r.Phase == EpubConversionPhase.Recompressing);
    }

    [Theory]
    [InlineData("garbage", "is not a readable archive.")]
    [InlineData("empty", "is missing the leading mimetype entry.")]
    [InlineData("mimetype-not-first", "is missing the leading mimetype entry.")]
    [InlineData("missing-entry", "is missing required entry 'META-INF/container.xml'.")]
    [InlineData("empty-entry", "contains an empty entry 'META-INF/container.xml'.")]
    public async Task ConvertToEpubAsync_WhenTheWrittenBookIsBroken_FailsAndRemovesIt(string corruption, string expectedMessage)
    {
        var cbz = CreateCbz("Broken Book.cbz", Page("001.jpg"));
        var outputDir = Path.Combine(_workDir, "broken-" + corruption);
        var tempPath = Path.Combine(outputDir, "Broken Book.epub.tmp");
        var corrupted = false;
        var progress = new SyncProgress(report =>
        {
            // Validation re-reads the finished temp file right after this report.
            if (report.Phase == EpubConversionPhase.Validating)
            {
                Corrupt(tempPath, corruption);
                corrupted = true;
            }
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ConvertToEpubAsync(
            new[] { cbz },
            outputDir,
            new EpubConversionOptions(Progress: progress)));

        Assert.True(corrupted);
        Assert.Equal($"Generated EPUB for Broken Book.cbz {expectedMessage}", ex.Message);
        Assert.Equal(corruption == "garbage", ex.InnerException is InvalidDataException);
        Assert.Empty(Directory.GetFiles(outputDir));
    }

    private static void Corrupt(string path, string corruption)
    {
        if (corruption == "garbage")
        {
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("this is not a zip archive"));
            return;
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        switch (corruption)
        {
            case "empty":
                break;
            case "mimetype-not-first":
                AddTextEntry(zip, "META-INF/container.xml", "<container/>");
                AddTextEntry(zip, "mimetype", "application/epub+zip");
                break;
            case "missing-entry":
                AddTextEntry(zip, "mimetype", "application/epub+zip");
                break;
            case "empty-entry":
                AddTextEntry(zip, "mimetype", "application/epub+zip");
                zip.CreateEntry("META-INF/container.xml");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
        }
    }

    private static void AddTextEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private string CreateCbz(string fileName, params (string Name, byte[] Data)[] entries)
    {
        var path = Path.Combine(_workDir, fileName);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            using var entryStream = entry.Open();
            entryStream.Write(data);
        }

        return path;
    }

    private static (string Name, byte[] Data) Page(string name) => (name, CreateImage(MagickFormat.Jpeg, 20, 30));

    private static byte[] CreateImage(MagickFormat format, int width, int height)
    {
        using var image = new MagickImage(MagickColors.SteelBlue, (uint)width, (uint)height) { Format = format };
        return image.ToByteArray();
    }

    /// <summary>
    /// A tiny PNG whose header declares <paramref name="width"/> x
    /// <paramref name="height"/> pixels: a "decompression bomb" that has to be
    /// rejected from its header alone.
    /// </summary>
    private static byte[] CreatePngDeclaring(uint width, uint height)
    {
        var png = CreateImage(MagickFormat.Png, 4, 4);

        // IHDR is always the first chunk: length (8..11), type (12..15), width
        // (16..19), height (20..23), five one-byte fields, then the CRC of the
        // type and data (29..32).
        Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), height);
        BinaryPrimitives.WriteUInt32BigEndian(
            png.AsSpan(29),
            ComicReaderServiceCoverageTests.StoredRar4.Crc32(png.AsSpan(12, 17)));
        return png;
    }

    /// <summary>
    /// Rewrites the compression method of <paramref name="entryName"/> in both
    /// its local and central directory headers.
    /// </summary>
    private static void SetCompressionMethod(string zipPath, string entryName, ushort method)
    {
        var bytes = File.ReadAllBytes(zipPath);
        var span = bytes.AsSpan();
        ReadOnlySpan<byte> endOfCentralDirectory = [0x50, 0x4B, 0x05, 0x06];
        var eocd = span.LastIndexOf(endOfCentralDirectory);
        Assert.True(eocd >= 0);

        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(span[(eocd + 10)..]);
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(eocd + 16)..]);
        var patched = false;
        for (var i = 0; i < entryCount; i++)
        {
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(span[(offset + 28)..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(span[(offset + 30)..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(span[(offset + 32)..]);
            var localHeader = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(offset + 42)..]);
            if (Encoding.UTF8.GetString(span.Slice(offset + 46, nameLength)) == entryName)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 10)..], method);
                BinaryPrimitives.WriteUInt16LittleEndian(span[(localHeader + 8)..], method);
                patched = true;
            }

            offset += 46 + nameLength + extraLength + commentLength;
        }

        Assert.True(patched, $"Entry '{entryName}' not found in {zipPath}");
        File.WriteAllBytes(zipPath, bytes);
    }

    private static string ReadEntry(ZipArchive archive, string name)
        => Encoding.UTF8.GetString(ReadBytes(archive, name));

    private static byte[] ReadBytes(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Delivers progress synchronously, on the converting thread.</summary>
    private sealed class SyncProgress(Action<EpubConversionProgress> onReport) : IProgress<EpubConversionProgress>
    {
        public void Report(EpubConversionProgress value) => onReport(value);
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger : ILogger<EpubConversionService>
    {
        private readonly List<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
            }
        }
    }
}
