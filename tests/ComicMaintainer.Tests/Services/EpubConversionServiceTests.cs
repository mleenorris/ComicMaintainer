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

    private static void WriteImageEntry(ZipArchive zip, string entryName)
    {
        using var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(20, 30);
        using var buffer = new MemoryStream();
        image.Save(buffer, new JpegEncoder());

        var entry = zip.CreateEntry(entryName);
        using var stream = entry.Open();
        var bytes = buffer.ToArray();
        stream.Write(bytes, 0, bytes.Length);
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
