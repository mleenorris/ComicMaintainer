using System.IO.Compression;
using System.Text;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace ComicMaintainer.Tests.Services;

public class EpubConversionServiceTests : IDisposable
{
    private readonly string _workDir;
    private readonly EpubConversionService _service;

    public EpubConversionServiceTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"epub-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _service = new EpubConversionService(new Mock<ILogger<EpubConversionService>>().Object);
    }

    [Fact]
    public async Task ConvertToEpubAsync_ProducesValidEpubStructure()
    {
        var cbz = CreateCbz("Series Name - Chapter 0001.cbz", pageCount: 3, includeComicInfo: true);
        var outputDir = Path.Combine(_workDir, "out");

        var epubPath = await _service.ConvertToEpubAsync(cbz, outputDir);

        Assert.True(File.Exists(epubPath));
        Assert.Equal(".epub", Path.GetExtension(epubPath));

        using var archive = ZipFile.OpenRead(epubPath);
        var names = archive.Entries.Select(e => e.FullName).ToList();

        // The mimetype entry must exist, come first, and be stored uncompressed.
        Assert.Equal("mimetype", archive.Entries[0].FullName);
        Assert.Equal(archive.Entries[0].Length, archive.Entries[0].CompressedLength);
        Assert.Equal("application/epub+zip", ReadEntry(archive, "mimetype"));

        Assert.Contains("META-INF/container.xml", names);
        Assert.Contains("OEBPS/content.opf", names);
        Assert.Contains("OEBPS/nav.xhtml", names);

        for (var i = 1; i <= 3; i++)
        {
            Assert.Contains($"OEBPS/page{i:D4}.xhtml", names);
            Assert.Contains($"OEBPS/images/page{i:D4}.jpg", names);
        }

        var opf = ReadEntry(archive, "OEBPS/content.opf");
        Assert.Contains("pre-paginated", opf);
        Assert.Contains("""<dc:title id="title">Series Name #001</dc:title>""", opf);
        Assert.Contains("""<itemref idref="page0003"/>""", opf);
        Assert.Contains("""properties="cover-image" """.TrimEnd(), opf);
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithoutComicInfo_UsesFilenameAsTitle()
    {
        var cbz = CreateCbz("Mystery Issue.cbz", pageCount: 1, includeComicInfo: false);

        var epubPath = await _service.ConvertToEpubAsync(cbz, Path.Combine(_workDir, "out2"));

        using var archive = ZipFile.OpenRead(epubPath);
        Assert.Contains("""<dc:title id="title">Mystery Issue</dc:title>""", ReadEntry(archive, "OEBPS/content.opf"));
    }

    [Fact]
    public async Task ConvertToEpubAsync_EscapesMetadataValues()
    {
        var cbz = CreateCbz(
            "Escaped.cbz",
            pageCount: 1,
            includeComicInfo: true,
            series: "Tom & Jerry <Deluxe>",
            number: "2");

        var epubPath = await _service.ConvertToEpubAsync(cbz, Path.Combine(_workDir, "out3"));

        using var archive = ZipFile.OpenRead(epubPath);
        var opf = ReadEntry(archive, "OEBPS/content.opf");
        Assert.Contains("Tom &amp; Jerry &lt;Deluxe&gt; #002", opf);
        Assert.DoesNotContain("<Deluxe>", opf);
    }

    [Fact]
    public async Task ConvertToEpubAsync_EncodesSeriesAndIssueNumberMetadata()
    {
        var cbz = CreateCbz("Numbered.cbz", pageCount: 1, includeComicInfo: true, number: "12.5");

        var epubPath = await _service.ConvertToEpubAsync(cbz, Path.Combine(_workDir, "out-number"));

        using var archive = ZipFile.OpenRead(epubPath);
        var opf = ReadEntry(archive, "OEBPS/content.opf");

        // Zero-padded in the title so readers without series support still sort correctly.
        Assert.Contains("""<dc:title id="title">Series Name #012.5</dc:title>""", opf);
        Assert.Contains("""<meta refines="#title" property="file-as">Series Name #012.5</meta>""", opf);
        Assert.Contains("""<meta property="belongs-to-collection" id="series">Series Name</meta>""", opf);
        Assert.Contains("""<meta refines="#series" property="collection-type">series</meta>""", opf);
        Assert.Contains("""<meta refines="#series" property="group-position">12.5</meta>""", opf);
        Assert.Contains("""<meta name="calibre:series" content="Series Name"/>""", opf);
        Assert.Contains("""<meta name="calibre:series_index" content="12.5"/>""", opf);
    }

    [Theory]
    [InlineData("7", "Series Name #007", "7")]
    [InlineData("007", "Series Name #007", "7")]
    [InlineData("#42", "Series Name #042", "42")]
    [InlineData("100", "Series Name #100", "100")]
    public async Task ConvertToEpubAsync_NormalizesDecoratedIssueNumbers(
        string number,
        string expectedTitle,
        string expectedPosition)
    {
        var cbz = CreateCbz($"decorated-{expectedPosition}.cbz", pageCount: 1, includeComicInfo: true, number: number);

        var epubPath = await _service.ConvertToEpubAsync(cbz, Path.Combine(_workDir, $"out-num-{expectedPosition}"));

        using var archive = ZipFile.OpenRead(epubPath);
        var opf = ReadEntry(archive, "OEBPS/content.opf");
        Assert.Contains($"""<dc:title id="title">{expectedTitle}</dc:title>""", opf);
        Assert.Contains($"""<meta refines="#series" property="group-position">{expectedPosition}</meta>""", opf);
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithNonNumericIssueNumber_KeepsItVerbatim()
    {
        var cbz = CreateCbz("annual.cbz", pageCount: 1, includeComicInfo: true, number: "Annual");

        var epubPath = await _service.ConvertToEpubAsync(cbz, Path.Combine(_workDir, "out-annual"));

        using var archive = ZipFile.OpenRead(epubPath);
        var opf = ReadEntry(archive, "OEBPS/content.opf");
        Assert.Contains("""<dc:title id="title">Series Name #Annual</dc:title>""", opf);
        Assert.DoesNotContain("group-position", opf);
        Assert.DoesNotContain("calibre:series_index", opf);
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithSeriesImage_EmbedsItAsTheCover()
    {
        var cbz = CreateCbz("Covered.cbz", pageCount: 2, includeComicInfo: true);
        var coverPath = Path.Combine(_workDir, "series-cover.jpg");
        await File.WriteAllBytesAsync(coverPath, CreateJpeg(60, 90));

        var epubPath = await _service.ConvertToEpubAsync(
            cbz,
            Path.Combine(_workDir, "out-cover"),
            new EpubConversionOptions(coverPath, "Series Name"));

        using var archive = ZipFile.OpenRead(epubPath);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("OEBPS/images/cover.jpg", names);
        Assert.Contains("OEBPS/cover.xhtml", names);

        var coverXhtml = ReadEntry(archive, "OEBPS/cover.xhtml");
        Assert.Contains("images/cover.jpg", coverXhtml);
        Assert.Contains("content=\"width=60, height=90\"", coverXhtml);

        var opf = ReadEntry(archive, "OEBPS/content.opf");
        Assert.Contains("""<item id="cover-image" href="images/cover.jpg" media-type="image/jpeg" properties="cover-image"/>""", opf);
        // The first page must no longer claim the cover-image property.
        Assert.Equal(1, CountOccurrences(opf, "properties=\"cover-image\""));
        Assert.Contains("""<itemref idref="cover-page" properties="rendition:page-spread-center"/>""", opf);
        Assert.True(
            opf.IndexOf("<itemref idref=\"cover-page\"", StringComparison.Ordinal) <
            opf.IndexOf("""<itemref idref="page0001"/>""", StringComparison.Ordinal));

        var nav = ReadEntry(archive, "OEBPS/nav.xhtml");
        Assert.Contains("epub:type=\"landmarks\"", nav);
        Assert.Contains("""<a epub:type="cover" href="cover.xhtml">Cover</a>""", nav);
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithMissingOrInvalidSeriesImage_FallsBackToFirstPage()
    {
        var cbz = CreateCbz("Fallback.cbz", pageCount: 2, includeComicInfo: true);
        var notAnImage = Path.Combine(_workDir, "broken-cover.jpg");
        await File.WriteAllTextAsync(notAnImage, "not an image");

        foreach (var (candidate, outDir) in new[]
                 {
                     (Path.Combine(_workDir, "missing-cover.jpg"), "out-missing"),
                     (notAnImage, "out-broken")
                 })
        {
            var epubPath = await _service.ConvertToEpubAsync(
                cbz,
                Path.Combine(_workDir, outDir),
                new EpubConversionOptions(candidate, "Series Name"));

            using var archive = ZipFile.OpenRead(epubPath);
            Assert.DoesNotContain("OEBPS/cover.xhtml", archive.Entries.Select(e => e.FullName));

            var opf = ReadEntry(archive, "OEBPS/content.opf");
            Assert.Contains("<item id=\"cover-image\" href=\"images/page0001.jpg\"", opf);
        }
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithEmbeddedCoverEntry_MakesItTheFirstPage()
    {
        // The series cover an archive carries sorts after the numbered pages,
        // so without special handling the book would open on page 1 and show
        // the series art at the very end.
        var cbz = Path.Combine(_workDir, "embedded-cover.cbz");
        using (var zip = ZipFile.Open(cbz, ZipArchiveMode.Create))
        {
            WriteImageEntry(zip, "001.jpg");
            WriteImageEntry(zip, "002.jpg");
            WriteImageEntry(zip, "cover.jpg", CreateJpeg(60, 90));
        }

        var epubPath = await _service.ConvertToEpubAsync(cbz, Path.Combine(_workDir, "out-embedded-cover"));

        using var archive = ZipFile.OpenRead(epubPath);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Equal(3, names.Count(n => n.StartsWith("OEBPS/images/page", StringComparison.Ordinal)));

        // The embedded cover is the first page, so it is also what the reader
        // shows as the book's cover image.
        Assert.Contains("content=\"width=60, height=90\"", ReadEntry(archive, "OEBPS/page0001.xhtml"));
        Assert.Contains(
            "<item id=\"cover-image\" href=\"images/page0001.jpg\"",
            ReadEntry(archive, "OEBPS/content.opf"));
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithEmbeddedCopyOfTheSeriesImage_DropsTheDuplicatePage()
    {
        var coverPath = Path.Combine(_workDir, "duplicate-cover.jpg");
        var coverBytes = CreateJpeg(60, 90);
        await File.WriteAllBytesAsync(coverPath, coverBytes);

        // ComicMaintainer embeds the cached series cover into the first archive
        // of a series, so the same image would otherwise appear twice in a row.
        var cbz = Path.Combine(_workDir, "duplicate-cover.cbz");
        using (var zip = ZipFile.Open(cbz, ZipArchiveMode.Create))
        {
            WriteImageEntry(zip, "001.jpg");
            WriteImageEntry(zip, "002.jpg");
            WriteImageEntry(zip, "cover.jpg", coverBytes);
        }

        var epubPath = await _service.ConvertToEpubAsync(
            cbz,
            Path.Combine(_workDir, "out-duplicate-cover"),
            new EpubConversionOptions(coverPath, "Series Name"));

        using var archive = ZipFile.OpenRead(epubPath);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("OEBPS/cover.xhtml", names);
        Assert.Equal(2, names.Count(n => n.StartsWith("OEBPS/images/page", StringComparison.Ordinal)));

        // Only the dedicated cover page carries the series art; the pages that
        // follow it are the comic's own.
        Assert.DoesNotContain("content=\"width=60, height=90\"", ReadEntry(archive, "OEBPS/page0001.xhtml"));
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithOnlyACoverEntry_KeepsItAsThePage()
    {
        var coverPath = Path.Combine(_workDir, "sole-cover.jpg");
        var coverBytes = CreateJpeg(60, 90);
        await File.WriteAllBytesAsync(coverPath, coverBytes);

        var cbz = Path.Combine(_workDir, "sole-cover.cbz");
        using (var zip = ZipFile.Open(cbz, ZipArchiveMode.Create))
        {
            WriteImageEntry(zip, "cover.jpg", coverBytes);
        }

        var epubPath = await _service.ConvertToEpubAsync(
            cbz,
            Path.Combine(_workDir, "out-sole-cover"),
            new EpubConversionOptions(coverPath, "Series Name"));

        using var archive = ZipFile.OpenRead(epubPath);
        // Dropping the only image would leave a book with no pages at all.
        Assert.Contains("OEBPS/images/page0001.jpg", archive.Entries.Select(e => e.FullName));
    }

    [Fact]
    public async Task ConvertToEpubAsync_KeepsNestedCoverEntriesInPageOrder()
    {
        // Only a root-level cover.* is series art; one inside a chapter folder
        // is that chapter's own page and must keep its place.
        var cbz = Path.Combine(_workDir, "nested-cover.cbz");
        using (var zip = ZipFile.Open(cbz, ZipArchiveMode.Create))
        {
            WriteImageEntry(zip, "chapter 1/001.jpg");
            WriteImageEntry(zip, "chapter 1/cover.jpg", CreateJpeg(60, 90));
        }

        var epubPath = await _service.ConvertToEpubAsync(cbz, Path.Combine(_workDir, "out-nested-cover"));

        using var archive = ZipFile.OpenRead(epubPath);
        Assert.DoesNotContain("content=\"width=60, height=90\"", ReadEntry(archive, "OEBPS/page0001.xhtml"));
        Assert.Contains("content=\"width=60, height=90\"", ReadEntry(archive, "OEBPS/page0002.xhtml"));
    }

    [Fact]
    public async Task ConvertToEpubAsync_CondensedBook_PlacesEachIssueCoverFirst()
    {
        var first = Path.Combine(_workDir, "Condensed - Chapter 0001.cbz");
        using (var zip = ZipFile.Open(first, ZipArchiveMode.Create))
        {
            WriteImageEntry(zip, "001.jpg");
            WriteImageEntry(zip, "cover.jpg", CreateJpeg(60, 90));
        }

        var second = Path.Combine(_workDir, "Condensed - Chapter 0002.cbz");
        using (var zip = ZipFile.Open(second, ZipArchiveMode.Create))
        {
            WriteImageEntry(zip, "001.jpg");
            WriteImageEntry(zip, "cover.jpg", CreateJpeg(70, 100));
        }

        var epubPath = await _service.ConvertToEpubAsync(
            new[] { first, second },
            Path.Combine(_workDir, "out-condensed-cover"),
            new EpubConversionOptions(Title: "Condensed 001-002"));

        using var archive = ZipFile.OpenRead(epubPath);
        Assert.Contains("content=\"width=60, height=90\"", ReadEntry(archive, "OEBPS/page0001.xhtml"));
        Assert.Contains("content=\"width=70, height=100\"", ReadEntry(archive, "OEBPS/page0003.xhtml"));
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithoutComicInfoSeries_UsesTheProvidedSeriesTitle()
    {
        var cbz = CreateCbz("No Series.cbz", pageCount: 1, includeComicInfo: false);

        var epubPath = await _service.ConvertToEpubAsync(
            cbz,
            Path.Combine(_workDir, "out-series-title"),
            new EpubConversionOptions(null, "Library Series"));

        using var archive = ZipFile.OpenRead(epubPath);
        var opf = ReadEntry(archive, "OEBPS/content.opf");
        Assert.Contains("""<meta property="belongs-to-collection" id="series">Library Series</meta>""", opf);
        Assert.Contains("""<dc:title id="title">Library Series</dc:title>""", opf);
    }

    [Fact]
    public async Task ConvertToEpubAsync_SortsPagesNaturally()
    {
        var cbz = Path.Combine(_workDir, "natural.cbz");
        using (var zip = ZipFile.Open(cbz, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "page10.jpg", "page2.jpg", "page1.jpg" })
            {
                WriteImageEntry(zip, name);
            }
        }

        var epubPath = await _service.ConvertToEpubAsync(cbz, Path.Combine(_workDir, "out4"));

        using var archive = ZipFile.OpenRead(epubPath);
        // page0002 is the second spine entry, so the source page2.jpg must map to it.
        var secondPage = ReadEntry(archive, "OEBPS/page0002.xhtml");
        Assert.Contains("images/page0002.jpg", secondPage);
        Assert.Equal(3, archive.Entries.Count(e => e.FullName.StartsWith("OEBPS/images/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ConvertToEpubAsync_IgnoresNonImageAndResourceForkEntries()
    {
        var cbz = Path.Combine(_workDir, "noise.cbz");
        using (var zip = ZipFile.Open(cbz, ZipArchiveMode.Create))
        {
            WriteImageEntry(zip, "001.jpg");
            WriteImageEntry(zip, "__MACOSX/002.jpg");
            zip.CreateEntry("readme.txt").Open().Dispose();
        }

        var epubPath = await _service.ConvertToEpubAsync(cbz, Path.Combine(_workDir, "out5"));

        using var archive = ZipFile.OpenRead(epubPath);
        Assert.Single(archive.Entries, e => e.FullName.StartsWith("OEBPS/images/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithNoImages_Throws()
    {
        var cbz = Path.Combine(_workDir, "empty.cbz");
        using (var zip = ZipFile.Open(cbz, ZipArchiveMode.Create))
        {
            zip.CreateEntry("readme.txt").Open().Dispose();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ConvertToEpubAsync(cbz, Path.Combine(_workDir, "out6")));
        Assert.False(Directory.Exists(Path.Combine(_workDir, "out6")) &&
                     Directory.EnumerateFiles(Path.Combine(_workDir, "out6")).Any());
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithMissingFile_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _service.ConvertToEpubAsync(Path.Combine(_workDir, "nope.cbz"), _workDir));
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithUnsupportedExtension_Throws()
    {
        var path = Path.Combine(_workDir, "not-a-comic.txt");
        await File.WriteAllTextAsync(path, "nope");

        await Assert.ThrowsAsync<NotSupportedException>(
            () => _service.ConvertToEpubAsync(path, _workDir));
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithUndecodablePage_ThrowsAndWritesNoEpub()
    {
        var cbz = Path.Combine(_workDir, "corrupt.cbz");
        using (var zip = ZipFile.Open(cbz, ZipArchiveMode.Create))
        {
            WriteImageEntry(zip, "001.jpg");

            // A page that is not a decodable image would render blank on the device.
            var entry = zip.CreateEntry("002.jpg");
            using var stream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes("not an image");
            stream.Write(bytes, 0, bytes.Length);
        }

        var outDir = Path.Combine(_workDir, "out-corrupt");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ConvertToEpubAsync(cbz, outDir));

        Assert.False(Directory.Exists(outDir) && Directory.EnumerateFiles(outDir).Any());
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithEmptyPageEntry_Throws()
    {
        var cbz = Path.Combine(_workDir, "empty-page.cbz");
        using (var zip = ZipFile.Open(cbz, ZipArchiveMode.Create))
        {
            WriteImageEntry(zip, "001.jpg");
            zip.CreateEntry("002.jpg").Open().Dispose();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ConvertToEpubAsync(cbz, Path.Combine(_workDir, "out-empty-page")));
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithUnsupportedPageFormat_ReencodesAsJpeg()
    {
        var cbz = Path.Combine(_workDir, "webp.cbz");
        using (var zip = ZipFile.Open(cbz, ZipArchiveMode.Create))
        {
            using var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(20, 30);
            using var buffer = new MemoryStream();
            image.Save(buffer, new SixLabors.ImageSharp.Formats.Webp.WebpEncoder());

            var entry = zip.CreateEntry("001.webp");
            using var stream = entry.Open();
            var bytes = buffer.ToArray();
            stream.Write(bytes, 0, bytes.Length);
        }

        var epubPath = await _service.ConvertToEpubAsync(cbz, Path.Combine(_workDir, "out-webp"));

        using var archive = ZipFile.OpenRead(epubPath);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("OEBPS/images/page0001.jpg", names);
        Assert.DoesNotContain("OEBPS/images/page0001.webp", names);
        Assert.Contains("media-type=\"image/jpeg\"", ReadEntry(archive, "OEBPS/content.opf"));
    }

    [Fact]
    public async Task ConvertToEpubAsync_WithMislabeledPageExtension_UsesTheRealFormat()
    {
        var cbz = Path.Combine(_workDir, "mislabeled.cbz");
        using (var zip = ZipFile.Open(cbz, ZipArchiveMode.Create))
        {
            using var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(20, 30);
            using var buffer = new MemoryStream();
            image.Save(buffer, new SixLabors.ImageSharp.Formats.Png.PngEncoder());

            // PNG data stored under a .jpg name: declaring image/jpeg makes readers show a blank page.
            var entry = zip.CreateEntry("001.jpg");
            using var stream = entry.Open();
            var bytes = buffer.ToArray();
            stream.Write(bytes, 0, bytes.Length);
        }

        var epubPath = await _service.ConvertToEpubAsync(cbz, Path.Combine(_workDir, "out-mislabeled"));

        using var archive = ZipFile.OpenRead(epubPath);
        Assert.Contains("OEBPS/images/page0001.png", archive.Entries.Select(e => e.FullName));
        Assert.Contains("media-type=\"image/png\"", ReadEntry(archive, "OEBPS/content.opf"));
    }

    [Fact]
    public async Task ConvertToEpubAsync_WhenOverSizeBudget_CompressesPagesToFit()
    {
        var cbz = Path.Combine(_workDir, "large.cbz");
        using (var zip = ZipFile.Open(cbz, ZipArchiveMode.Create))
        {
            for (var i = 1; i <= 3; i++)
            {
                WriteImageEntry(zip, $"{i:D3}.png", CreateNoisyPng(1200, 1600));
            }
        }

        var uncompressed = await _service.ConvertToEpubAsync(
            cbz,
            Path.Combine(_workDir, "out-nobudget"),
            new EpubConversionOptions());
        var uncompressedLength = new FileInfo(uncompressed).Length;

        var budget = uncompressedLength / 4;
        var compressed = await _service.ConvertToEpubAsync(
            cbz,
            Path.Combine(_workDir, "out-budget"),
            new EpubConversionOptions(MaxSizeBytes: budget));

        Assert.True(
            new FileInfo(compressed).Length <= budget,
            $"Expected the book to fit in {budget} bytes, got {new FileInfo(compressed).Length}.");

        using var archive = ZipFile.OpenRead(compressed);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        // Compressed variants are always re-encoded as JPEG.
        Assert.Contains("OEBPS/images/page0001.jpg", names);
        Assert.Equal(3, names.Count(n => n.StartsWith("OEBPS/images/", StringComparison.Ordinal)));

        var opf = ReadEntry(archive, "OEBPS/content.opf");
        Assert.Contains("""<itemref idref="page0003"/>""", opf);
    }

    [Fact]
    public async Task ConvertToEpubAsync_WhenWithinSizeBudget_KeepsOriginalPageData()
    {
        var cbz = CreateCbz("Small.cbz", pageCount: 2, includeComicInfo: true);

        var epubPath = await _service.ConvertToEpubAsync(
            cbz,
            Path.Combine(_workDir, "out-within-budget"),
            new EpubConversionOptions(MaxSizeBytes: 50L * 1024 * 1024));

        using var archive = ZipFile.OpenRead(epubPath);
        var page = archive.GetEntry("OEBPS/images/page0001.jpg");
        Assert.NotNull(page);

        using var source = ZipFile.OpenRead(cbz);
        Assert.Equal(source.GetEntry("001.jpg")!.Length, page!.Length);
    }

    [Fact]
    public async Task ConvertToEpubAsync_CondensesSeveralIssuesIntoOneBook()
    {
        var first = CreateCbz("Series Name - Chapter 0001.cbz", pageCount: 2, includeComicInfo: true, number: "1");
        var second = CreateCbz("Series Name - Chapter 0002.cbz", pageCount: 3, includeComicInfo: true, number: "2");
        var outputDir = Path.Combine(_workDir, "condensed");

        var epubPath = await _service.ConvertToEpubAsync(
            new[] { first, second },
            outputDir,
            new EpubConversionOptions(Title: "Series Name 001-002", OutputFileName: "Series Name 001-002"));

        Assert.Equal("Series Name 001-002.epub", Path.GetFileName(epubPath));

        using var archive = ZipFile.OpenRead(epubPath);
        var names = archive.Entries.Select(e => e.FullName).ToList();

        // Pages are numbered continuously across the issues, in the order the
        // issues were supplied.
        for (var i = 1; i <= 5; i++)
        {
            Assert.Contains($"OEBPS/page{i:D4}.xhtml", names);
        }
        Assert.DoesNotContain("OEBPS/page0006.xhtml", names);

        var opf = ReadEntry(archive, "OEBPS/content.opf");
        Assert.Contains("""<dc:title id="title">Series Name 001-002</dc:title>""", opf);
        // The book spans a range of issues, so it must not be filed under a
        // single issue number.
        Assert.DoesNotContain("calibre:series_index", opf);

        var nav = ReadEntry(archive, "OEBPS/nav.xhtml");
        Assert.Contains("""<a href="page0001.xhtml">Series Name #001</a>""", nav);
        Assert.Contains("""<a href="page0003.xhtml">Series Name #002</a>""", nav);
    }

    [Fact]
    public async Task ConvertToEpubAsync_RejectsAnEmptyFileList()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ConvertToEpubAsync(Array.Empty<string>(), Path.Combine(_workDir, "none")));
    }

    [Fact]
    public async Task ConvertToEpubAsync_SanitizesTheRequestedFileName()
    {
        var cbz = CreateCbz("Series Name - Chapter 0001.cbz", pageCount: 1, includeComicInfo: false);

        var epubPath = await _service.ConvertToEpubAsync(
            new[] { cbz },
            Path.Combine(_workDir, "sanitized"),
            new EpubConversionOptions(OutputFileName: "../../escape/Series 001-005"));

        Assert.Equal(
            Path.GetFullPath(Path.Combine(_workDir, "sanitized")),
            Path.GetFullPath(Path.GetDirectoryName(epubPath)!));
        Assert.DoesNotContain("..", Path.GetFileName(epubPath));
    }

    [Fact]
    public async Task ConvertToEpubAsync_ReportsProgressForEveryPage()
    {
        var first = CreateCbz("Progress - Chapter 0001.cbz", pageCount: 2, includeComicInfo: true, number: "1");
        var second = CreateCbz("Progress - Chapter 0002.cbz", pageCount: 3, includeComicInfo: true, number: "2");
        var reports = new List<EpubConversionProgress>();

        await _service.ConvertToEpubAsync(
            new[] { first, second },
            Path.Combine(_workDir, "progress"),
            new EpubConversionOptions(
                Title: "Progress 001-002",
                Progress: new CollectingProgress(reports)));

        // Indexing the sources comes first, and the page total is only known
        // once every archive has been opened.
        Assert.Contains(reports, r => r.Phase == EpubConversionPhase.Reading);

        var written = reports.Where(r => r.Phase == EpubConversionPhase.Writing).ToList();
        Assert.Equal(5, written.Count);
        Assert.Equal(Enumerable.Range(1, 5), written.Select(r => r.CompletedPages));
        Assert.All(written, r => Assert.Equal(5, r.TotalPages));
        Assert.All(written, r => Assert.Equal(2, r.TotalIssues));
        Assert.Equal(Path.GetFileName(second), written[^1].CurrentIssue);

        // Without a size budget there is exactly one compression pass, and the
        // book is verified before it is handed back.
        Assert.All(reports, r => Assert.Equal(1, r.TotalPasses));
        Assert.Equal(EpubConversionPhase.Validating, reports[^1].Phase);
    }

    [Fact]
    public async Task ConvertToEpubAsync_ProgressCallbackFailureDoesNotFailTheConversion()
    {
        var cbz = CreateCbz("Throwing.cbz", pageCount: 2, includeComicInfo: false);

        var epubPath = await _service.ConvertToEpubAsync(
            new[] { cbz },
            Path.Combine(_workDir, "throwing-progress"),
            new EpubConversionOptions(Progress: new ThrowingProgress()));

        Assert.True(File.Exists(epubPath));
    }

    private sealed class CollectingProgress : IProgress<EpubConversionProgress>
    {
        private readonly List<EpubConversionProgress> _reports;

        public CollectingProgress(List<EpubConversionProgress> reports) => _reports = reports;

        public void Report(EpubConversionProgress value) => _reports.Add(value);
    }

    private sealed class ThrowingProgress : IProgress<EpubConversionProgress>
    {
        public void Report(EpubConversionProgress value) => throw new InvalidOperationException("boom");
    }

    private string CreateCbz(
        string fileName,
        int pageCount,
        bool includeComicInfo,
        string series = "Series Name",
        string number = "1")
    {
        var path = Path.Combine(_workDir, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        if (includeComicInfo)
        {
            var entry = zip.CreateEntry("ComicInfo.xml");
            using var stream = entry.Open();
            var xml = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <ComicInfo>
                  <Series>{System.Security.SecurityElement.Escape(series)}</Series>
                  <Number>{number}</Number>
                  <Writer>Jane Doe</Writer>
                </ComicInfo>
                """;
            var bytes = Encoding.UTF8.GetBytes(xml);
            stream.Write(bytes, 0, bytes.Length);
        }

        for (var i = 1; i <= pageCount; i++)
        {
            WriteImageEntry(zip, $"{i:D3}.jpg");
        }

        return path;
    }

    private static byte[] CreateJpeg(int width, int height)
    {
        using var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);
        using var buffer = new MemoryStream();
        image.Save(buffer, new JpegEncoder());
        return buffer.ToArray();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static void WriteImageEntry(ZipArchive zip, string entryName, byte[]? content = null)
    {
        byte[] bytes;
        if (content is not null)
        {
            bytes = content;
        }
        else
        {
            using var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(20, 30);
            using var buffer = new MemoryStream();
            image.Save(buffer, new JpegEncoder());
            bytes = buffer.ToArray();
        }

        var entry = zip.CreateEntry(entryName);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// A noisy PNG: large and incompressible, so the size-budget tiers have
    /// something real to shrink.
    /// </summary>
    private static byte[] CreateNoisyPng(int width, int height)
    {
        using var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);
        var random = new Random(1234);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = new SixLabors.ImageSharp.PixelFormats.Rgba32(
                        (byte)random.Next(256),
                        (byte)random.Next(256),
                        (byte)random.Next(256),
                        255);
                }
            }
        });

        using var buffer = new MemoryStream();
        image.Save(buffer, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        return buffer.ToArray();
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
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
}
