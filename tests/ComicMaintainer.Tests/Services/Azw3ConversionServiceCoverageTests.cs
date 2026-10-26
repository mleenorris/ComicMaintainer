using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using ImageMagick;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Feeds <see cref="Azw3ConversionService"/> hand-built intermediate EPUBs so the
/// read-back of the package (and the KF8 writer behind it) can be exercised with
/// layouts the EPUB converter itself never produces.
/// </summary>
public sealed class Azw3ConversionServiceCoverageTests : IDisposable
{
    private readonly string _workDir;
    private readonly Mock<IEpubConversionService> _epub = new();
    private readonly Azw3ConversionService _service;

    public Azw3ConversionServiceCoverageTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"azw3-coverage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _service = new Azw3ConversionService(_epub.Object, new Mock<ILogger<Azw3ConversionService>>().Object);
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
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ConvertToAzw3Async_RejectsABlankComicPath(string comicFilePath)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _service.ConvertToAzw3Async(comicFilePath, _workDir));

        Assert.Equal("comicFilePath", ex.ParamName);
        VerifyNoIntermediateBookWasRequested();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ConvertToAzw3Async_RejectsABlankOutputDirectory(string outputDirectory)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _service.ConvertToAzw3Async(new[] { "Issue 001.cbz" }, outputDirectory));

        Assert.Equal("outputDirectory", ex.ParamName);
        VerifyNoIntermediateBookWasRequested();
    }

    [Theory]
    [InlineData("no-container", "The intermediate book is missing the entry 'META-INF/container.xml' and cannot be converted to AZW3.")]
    [InlineData("no-rootfiles", "The intermediate book has no package document and cannot be converted to AZW3.")]
    [InlineData("blank-rootfile", "The intermediate book has no package document and cannot be converted to AZW3.")]
    [InlineData("rootfile-without-path", "The intermediate book has no package document and cannot be converted to AZW3.")]
    [InlineData("missing-package", "The intermediate book is missing the entry 'OEBPS/content.opf' and cannot be converted to AZW3.")]
    [InlineData("missing-page", "The intermediate book is missing the entry 'OEBPS/text/page1.xhtml' and cannot be converted to AZW3.")]
    [InlineData("missing-image", "The intermediate book is missing the entry 'OEBPS/images/page1.jpg' and cannot be converted to AZW3.")]
    [InlineData("no-manifest", "No pages could be read back from the intermediate book for Broken.azw3")]
    [InlineData("no-spine", "No pages could be read back from the intermediate book for Broken.azw3")]
    [InlineData("no-images", "No pages could be read back from the intermediate book for Broken.azw3")]
    public async Task ConvertToAzw3Async_RejectsAnIntermediateBookThatCannotBeReadBack(string kind, string expectedMessage)
    {
        var book = SimpleBook(Metadata(title: "Broken"));
        var entries = kind switch
        {
            "no-container" => book.Where(e => e.Name != "META-INF/container.xml").ToArray(),
            "no-rootfiles" => Replace(book, Text(
                "META-INF/container.xml",
                "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"/>")),
            "blank-rootfile" => Replace(book, Text("META-INF/container.xml", Container("  "))),
            "rootfile-without-path" => Replace(book, Text(
                "META-INF/container.xml",
                "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">" +
                "<rootfiles><rootfile media-type=\"application/oebps-package+xml\"/></rootfiles></container>")),
            "missing-package" => book.Where(e => e.Name != "OEBPS/content.opf").ToArray(),
            "missing-page" => book.Where(e => e.Name != "OEBPS/text/page1.xhtml").ToArray(),
            "missing-image" => book.Where(e => e.Name != "OEBPS/images/page1.jpg").ToArray(),
            "no-manifest" => Replace(book, Text(
                "OEBPS/content.opf",
                Package(Metadata(title: "Broken"), manifest: string.Empty, spine: SimpleSpine))),
            "no-spine" => Replace(book, Text(
                "OEBPS/content.opf",
                Package(Metadata(title: "Broken"), SimpleManifest, spine: string.Empty))),
            "no-images" => Replace(book, Text("OEBPS/text/page1.xhtml", PageDocument("Page One", imageSource: null))),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        UseIntermediateBook("Broken", entries);
        var outputDir = Path.Combine(_workDir, "out");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ConvertToAzw3Async(new[] { "Broken.cbz" }, outputDir));

        Assert.StartsWith(expectedMessage, ex.Message, StringComparison.Ordinal);

        // Neither the intermediate book nor a partial AZW3 is left behind.
        Assert.Empty(Directory.GetFileSystemEntries(outputDir));
    }

    [Fact]
    public async Task ConvertToAzw3Async_ReadsBackAnUnusuallyLaidOutIntermediateBook()
    {
        var png = Image(MagickFormat.Png, 10, 20);
        var jpeg = Image(MagickFormat.Jpeg, 12, 18);
        UseIntermediateBook(
            "Quirky",
            Text("META-INF/container.xml", Container("content.opf")),
            Text("content.opf", Package(
                // No <metadata>: the title falls back to the file name and the
                // language to English.
                metadata: string.Empty,
                manifest: "<manifest>" +
                          "<item id=\"p1\" href=\"./text/../text/page1.xhtml\" media-type=\"application/xhtml+xml\"/>" +
                          "<item id=\"p2\" href=\"text/page2.xhtml\"/>" +
                          "<item id=\"blank\" href=\"text/blank.xhtml\" media-type=\"application/xhtml+xml\"/>" +
                          "<item href=\"text/no-id.xhtml\" media-type=\"application/xhtml+xml\"/>" +
                          "<item id=\"no-href\" media-type=\"application/xhtml+xml\"/>" +
                          "<item id=\"img1\" href=\"images/one.png\" media-type=\"image/png\"/>" +
                          "</manifest>",
                spine: "<spine>" +
                       "<itemref/>" +
                       "<itemref idref=\"not-in-manifest\"/>" +
                       "<itemref idref=\"no-href\"/>" +
                       "<itemref idref=\"blank\"/>" +
                       "<itemref idref=\"p1\"/>" +
                       "<itemref idref=\"p2\"/>" +
                       "</spine>")),
            Text("text/page1.xhtml", PageDocument("First &amp; Best", "../images/one.png")),
            // ".." past the container root is dropped and "." is ignored. The
            // image is not in the manifest, so it is assumed to be a JPEG.
            Text("text/page2.xhtml", PageDocument(title: null, "../../../images/./two.jpg")),
            Text("text/blank.xhtml", PageDocument("Blank", imageSource: null)),
            ("images/one.png", png),
            ("images/two.jpg", jpeg));
        var outputDir = Path.Combine(_workDir, "out");

        var path = await _service.ConvertToAzw3Async(new[] { "Quirky.cbz" }, outputDir);

        Assert.Equal(Path.Combine(outputDir, "Quirky.azw3"), path);
        Assert.Equal(new[] { "Quirky.azw3" }, Directory.GetFileSystemEntries(outputDir).Select(Path.GetFileName));

        var book = Azw3Reader.Read(path);
        Assert.Equal(2, book.Resources.Count);
        Assert.Equal(png, book.Resources[0]);
        Assert.Equal(jpeg, book.Resources[1]);
        Assert.StartsWith("Quirky", book.Title, StringComparison.Ordinal);
        Assert.Equal(book.Title, book.Exth[503]);
        Assert.Equal("en", book.Exth[524]);
        Assert.Equal(9u, ReadLanguageCode(path));
        Assert.False(book.Exth.ContainsKey(100));
        Assert.False(book.Exth.ContainsKey(101));
        Assert.Equal("10x20", book.Exth[126]);

        var documents = book.RebuildDocuments();
        Assert.Equal(2, documents.Count);
        Assert.Contains("<title>First &amp; Best</title>", documents[0], StringComparison.Ordinal);
        Assert.Contains("?mime=image/png\"", documents[0], StringComparison.Ordinal);
        Assert.Contains("width=\"10\" height=\"20\"", documents[0], StringComparison.Ordinal);
        Assert.Contains("<title>Page 2</title>", documents[1], StringComparison.Ordinal);
        Assert.Contains("?mime=image/jpeg\"", documents[1], StringComparison.Ordinal);
        Assert.Contains("width=\"12\" height=\"18\"", documents[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConvertToAzw3Async_FallsBackWhenThePackageMetadataIsEmpty()
    {
        UseIntermediateBook("Bare", SimpleBook(Metadata()));
        var outputDir = Path.Combine(_workDir, "out");

        var path = await _service.ConvertToAzw3Async(new[] { "Bare.cbz" }, outputDir);

        var book = Azw3Reader.Read(path);
        Assert.StartsWith("Bare", book.Title, StringComparison.Ordinal);
        Assert.Equal("en", book.Exth[524]);
        Assert.Equal(9u, ReadLanguageCode(path));
        Assert.False(book.Exth.ContainsKey(100));
        Assert.False(book.Exth.ContainsKey(101));
        Assert.Single(book.Resources);
    }

    [Theory]
    [InlineData("ar", 1u)]
    [InlineData("zh-Hant", 4u)]
    [InlineData("cs", 5u)]
    [InlineData("da", 6u)]
    [InlineData("de-DE", 7u)]
    [InlineData("el", 8u)]
    [InlineData("en-GB", 9u)]
    [InlineData("es", 10u)]
    [InlineData("fi", 11u)]
    [InlineData("fr-CA", 12u)]
    [InlineData("he", 13u)]
    [InlineData("hu", 14u)]
    [InlineData("it", 16u)]
    [InlineData("ja", 17u)]
    [InlineData("ko", 18u)]
    [InlineData("nl", 19u)]
    [InlineData("no", 20u)]
    [InlineData("pl", 21u)]
    [InlineData("PT-br", 22u)]
    [InlineData("ru", 25u)]
    [InlineData("sv", 29u)]
    [InlineData("th", 30u)]
    [InlineData("tr", 31u)]
    [InlineData("xx", 9u)]
    [InlineData("eng", 9u)]
    public async Task ConvertToAzw3Async_MapsTheBookLanguageOntoTheMobiLanguageCode(string language, uint expectedCode)
    {
        UseIntermediateBook("Language", SimpleBook(Metadata(
            title: "Language Book",
            creator: "Writer",
            publisher: "Publisher &amp; Co",
            language: language)));
        var outputDir = Path.Combine(_workDir, "out");

        var path = await _service.ConvertToAzw3Async(new[] { "Language.cbz" }, outputDir);

        var book = Azw3Reader.Read(path);
        Assert.Equal(expectedCode, ReadLanguageCode(path));
        Assert.Equal(language, book.Exth[524]);
        Assert.Equal("Language Book", book.Title);
        Assert.Equal("Language Book", book.Exth[503]);
        Assert.Equal("Writer", book.Exth[100]);
        Assert.Equal("Publisher & Co", book.Exth[101]);
    }

    [Fact]
    public async Task ConvertToAzw3Async_TreatsABlankLanguageAsEnglish()
    {
        UseIntermediateBook("Blank Language", SimpleBook(Metadata(title: "Blank Language", language: "  ")));
        var outputDir = Path.Combine(_workDir, "out");

        var path = await _service.ConvertToAzw3Async(new[] { "Blank Language.cbz" }, outputDir);

        Assert.Equal("en", Azw3Reader.Read(path).Exth[524]);
        Assert.Equal(9u, ReadLanguageCode(path));
    }

    [Fact]
    public async Task ConvertToAzw3Async_CarriesMultibyteCharactersAcrossTextRecordBoundaries()
    {
        // A 5-byte unit (a 4-byte character plus an ASCII letter) repeated well
        // past five 4096-byte boundaries puts every unit offset on some
        // boundary, so records end 0, 1, 2 and 3 bytes into a character.
        var title = string.Concat(Enumerable.Repeat("\U0001D11Ea", 5000));
        UseIntermediateBook("Music", SimpleBook(Metadata(title: "Music"), pageTitle: title));
        var outputDir = Path.Combine(_workDir, "out");

        var path = await _service.ConvertToAzw3Async(new[] { "Music.cbz" }, outputDir);

        var overlaps = ReadTextRecordOverlaps(path);
        Assert.Contains(1, overlaps);
        Assert.Contains(2, overlaps);
        Assert.Contains(3, overlaps);
        Assert.Equal(0, overlaps[^1]);

        var document = Assert.Single(Azw3Reader.Read(path).RebuildDocuments());
        Assert.Contains($"<title>{title}</title>", document, StringComparison.Ordinal);
        Assert.Contains($"alt=\"{title}\"", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConvertToAzw3Async_RemovesThePartialBookWhenItCannotBeMovedIntoPlace()
    {
        UseIntermediateBook("Blocked", SimpleBook(Metadata(title: "Blocked")));
        var outputDir = Path.Combine(_workDir, "out");
        var blocker = Path.Combine(outputDir, "Blocked.azw3");
        Directory.CreateDirectory(blocker);

        await Assert.ThrowsAnyAsync<IOException>(
            () => _service.ConvertToAzw3Async(new[] { "Blocked.cbz" }, outputDir));

        Assert.True(Directory.Exists(blocker));
        Assert.Equal(new[] { "Blocked.azw3" }, Directory.GetFileSystemEntries(outputDir).Select(Path.GetFileName));
    }

    private const string SimpleManifest =
        "<manifest>" +
        "<item id=\"page1\" href=\"text/page1.xhtml\" media-type=\"application/xhtml+xml\"/>" +
        "<item id=\"img1\" href=\"images/page1.jpg\" media-type=\"image/jpeg\"/>" +
        "</manifest>";

    private const string SimpleSpine = "<spine><itemref idref=\"page1\"/></spine>";

    private void UseIntermediateBook(string bookName, params (string Name, byte[] Data)[] entries)
    {
        _epub.Setup(e => e.ConvertToEpubAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string>(),
                It.IsAny<EpubConversionOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<string>, string, EpubConversionOptions?, CancellationToken>((_, stagingDirectory, _, _) =>
            {
                Directory.CreateDirectory(stagingDirectory);
                var path = Path.Combine(stagingDirectory, bookName + ".epub");
                using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
                foreach (var (name, data) in entries)
                {
                    using var stream = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
                    stream.Write(data);
                }

                return Task.FromResult(path);
            });
    }

    private void VerifyNoIntermediateBookWasRequested()
    {
        _epub.Verify(e => e.ConvertToEpubAsync(
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(),
            It.IsAny<EpubConversionOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static (string Name, byte[] Data)[] SimpleBook(string metadata, string pageTitle = "Page One") => new[]
    {
        Text("META-INF/container.xml", Container("OEBPS/content.opf")),
        Text("OEBPS/content.opf", Package(metadata, SimpleManifest, SimpleSpine)),
        Text("OEBPS/text/page1.xhtml", PageDocument(pageTitle, "../images/page1.jpg")),
        ("OEBPS/images/page1.jpg", Image(MagickFormat.Jpeg, 16, 24))
    };

    private static (string Name, byte[] Data)[] Replace(
        (string Name, byte[] Data)[] entries,
        (string Name, byte[] Data) replacement) =>
        entries.Select(e => e.Name == replacement.Name ? replacement : e).ToArray();

    private static (string Name, byte[] Data) Text(string name, string content) =>
        (name, Encoding.UTF8.GetBytes(content));

    private static string Container(string fullPath) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">" +
        $"<rootfiles><rootfile full-path=\"{fullPath}\" media-type=\"application/oebps-package+xml\"/></rootfiles>" +
        "</container>";

    private static string Package(string metadata, string manifest, string spine) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<package xmlns=\"http://www.idpf.org/2007/opf\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" version=\"3.0\">" +
        metadata + manifest + spine +
        "</package>";

    private static string Metadata(
        string? title = null,
        string? creator = null,
        string? publisher = null,
        string? language = null)
    {
        static string Element(string name, string? value) =>
            value is null ? string.Empty : $"<dc:{name}>{value}</dc:{name}>";

        return "<metadata>" +
               "<dc:identifier id=\"uid\">urn:uuid:3f1c2a4e-0000-4000-8000-000000000000</dc:identifier>" +
               Element("title", title) +
               Element("creator", creator) +
               Element("publisher", publisher) +
               Element("language", language) +
               "</metadata>";
    }

    private static string PageDocument(string? title, string? imageSource) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head>" +
        (title is null ? string.Empty : $"<title>{title}</title>") +
        "</head><body>" +
        (imageSource is null ? "<p>No image on this page</p>" : $"<div><img alt=\"page\" src=\"{imageSource}\"/></div>") +
        "</body></html>";

    private static byte[] Image(MagickFormat format, uint width, uint height)
    {
        using var image = new MagickImage(MagickColors.SteelBlue, width, height) { Format = format };
        return image.ToByteArray();
    }

    /// <summary>Reads the MOBI header's language code (offset 92 of record 0).</summary>
    private static uint ReadLanguageCode(string path)
    {
        var raw = File.ReadAllBytes(path);
        var record0 = (int)BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(78, 4));
        return BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(record0 + 92, 4));
    }

    /// <summary>
    /// Returns the trailing multibyte-overlap length every text record ends
    /// with (the last byte of records 1..lastTextRecord).
    /// </summary>
    private static List<int> ReadTextRecordOverlaps(string path)
    {
        var raw = File.ReadAllBytes(path);
        var recordCount = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(0x4C, 2));
        var offsets = Enumerable.Range(0, recordCount)
            .Select(i => (int)BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(78 + (8 * i), 4)))
            .Append(raw.Length)
            .ToList();
        var lastTextRecord = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(offsets[0] + 8, 2));

        return Enumerable.Range(1, lastTextRecord)
            .Select(record => (int)raw[offsets[record + 1] - 1])
            .ToList();
    }
}
