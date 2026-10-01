using System.IO.Compression;
using System.Text;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Verifies the generated AZW3 books by parsing them back the way a Kindle
/// reader does: the PalmDB records, the MOBI header, the skeleton/chunk indices
/// and the rebuilt page documents.
/// </summary>
public class Azw3ConversionServiceTests : IDisposable
{
    private readonly string _workDir;
    private readonly Azw3ConversionService _service;

    public Azw3ConversionServiceTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"azw3-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);

        var epub = new EpubConversionService(new Mock<ILogger<EpubConversionService>>().Object);
        _service = new Azw3ConversionService(epub, new Mock<ILogger<Azw3ConversionService>>().Object);
    }

    [Fact]
    public async Task ConvertToAzw3Async_ProducesAKindleFormat8Book()
    {
        var cbz = CreateCbz("Series Name - Chapter 0001.cbz", pageCount: 3);
        var outputDir = Path.Combine(_workDir, "out");

        var path = await _service.ConvertToAzw3Async(cbz, outputDir);

        Assert.True(File.Exists(path));
        Assert.Equal(".azw3", Path.GetExtension(path));

        // The intermediate EPUB must not be left behind next to the book.
        Assert.Equal(new[] { Path.GetFileName(path) }, Directory.GetFileSystemEntries(outputDir).Select(Path.GetFileName));

        var book = Azw3Reader.Read(path);

        Assert.Equal("BOOKMOBI", book.TypeAndCreator);
        Assert.Equal("MOBI", book.Identifier);
        Assert.Equal(8u, book.FileVersion);
        Assert.Equal(8u, book.MinimumVersion);
        Assert.Equal(1, book.Compression);
        Assert.Equal(4096, book.RecordSize);
        Assert.Equal(65001u, book.TextEncoding);
        Assert.Equal(1u, book.ExtraDataFlags);
        Assert.Equal("Series Name #001", book.Title);

        // Every page image is stored in its own resource record.
        Assert.Equal(3, book.Resources.Count);
        foreach (var resource in book.Resources)
        {
            var info = Image.Identify(resource);
            Assert.Equal(20, info.Width);
            Assert.Equal(30, info.Height);
        }

        Assert.Equal("3", book.Exth[125]);
        Assert.Equal("true", book.Exth[122]);
        Assert.Equal("comic", book.Exth[123]);
        Assert.Equal("20x30", book.Exth[126]);
        Assert.Equal("EBOK", book.Exth[501]);
        Assert.Equal("Series Name #001", book.Exth[503]);
        Assert.Equal("Jane Doe", book.Exth[100]);

        // The text records must rebuild into one document per page.
        var documents = book.RebuildDocuments();
        Assert.Equal(3, documents.Count);

        for (var i = 0; i < documents.Count; i++)
        {
            var document = documents[i];
            Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", document, StringComparison.Ordinal);
            Assert.EndsWith("</html>\n", document, StringComparison.Ordinal);
            Assert.Contains("<meta name=\"viewport\" content=\"width=20, height=30\"/>", document, StringComparison.Ordinal);

            // Resource references are 1-based and base-32 encoded.
            Assert.Contains($"<img src=\"kindle:embed:000{i + 1}?mime=image/jpeg\"", document, StringComparison.Ordinal);

            // The chunk must land inside the body, not between arbitrary tags.
            var bodyStart = document.IndexOf("<body ", StringComparison.Ordinal);
            var bodyEnd = document.IndexOf("</body>", StringComparison.Ordinal);
            Assert.InRange(document.IndexOf("<div ", StringComparison.Ordinal), bodyStart, bodyEnd);
        }
    }

    [Fact]
    public async Task ConvertToAzw3Async_SplitsLargeIndicesAcrossBoundedRecords()
    {
        const int pageCount = 3500;
        var cbz = CreateCbz("Series Name - Chapter 0001.cbz", pageCount);
        var path = await _service.ConvertToAzw3Async(cbz, Path.Combine(_workDir, "large"));

        var book = Azw3Reader.Read(path);

        Assert.Equal(pageCount, book.Resources.Count);
        Assert.Equal(pageCount, book.RebuildDocuments().Count);
        Assert.Equal(2, book.IndexDataRecordCounts.Count);
        Assert.All(book.IndexDataRecordCounts, count => Assert.True(count > 1));
    }

    [Fact]
    public async Task ConvertToAzw3Async_CondensesSeveralIssuesIntoOneBook()
    {
        var first = CreateCbz("Series Name - Chapter 0001.cbz", pageCount: 2);
        var second = CreateCbz("Series Name - Chapter 0002.cbz", pageCount: 1, number: "2");
        var outputDir = Path.Combine(_workDir, "condensed");

        var path = await _service.ConvertToAzw3Async(
            new[] { first, second },
            outputDir,
            new EpubConversionOptions(Title: "Series Name #001-002", OutputFileName: "Series Name 001-002"));

        Assert.Equal("Series Name 001-002.azw3", Path.GetFileName(path));

        var book = Azw3Reader.Read(path);
        Assert.Equal("Series Name #001-002", book.Title);
        Assert.Equal(3, book.Resources.Count);
        Assert.Equal(3, book.RebuildDocuments().Count);
    }

    [Fact]
    public async Task ConvertToAzw3Async_EmbedsTheSeriesCoverAsTheFirstPage()
    {
        var cbz = CreateCbz("Series Name - Chapter 0001.cbz", pageCount: 1);
        var coverPath = Path.Combine(_workDir, "series-cover.jpg");
        File.WriteAllBytes(coverPath, CreateJpeg(40, 60));

        var path = await _service.ConvertToAzw3Async(
            cbz,
            Path.Combine(_workDir, "cover"),
            new EpubConversionOptions(SeriesImagePath: coverPath, SeriesTitle: "Series Name"));

        var book = Azw3Reader.Read(path);

        Assert.Equal(2, book.Resources.Count);
        var cover = Image.Identify(book.Resources[0]);
        Assert.Equal(40, cover.Width);
        Assert.Equal(60, cover.Height);
        Assert.Equal("40x60", book.Exth[126]);
    }

    [Fact]
    public async Task ConvertToAzw3Async_RejectsUnsupportedSources()
    {
        var text = Path.Combine(_workDir, "notes.txt");
        File.WriteAllText(text, "not a comic");

        await Assert.ThrowsAsync<NotSupportedException>(
            () => _service.ConvertToAzw3Async(text, Path.Combine(_workDir, "bad")));

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.ConvertToAzw3Async(Array.Empty<string>(), Path.Combine(_workDir, "bad")));
    }

    private string CreateCbz(string fileName, int pageCount, string number = "1")
    {
        var path = Path.Combine(_workDir, fileName);
        using var stream = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        var entry = zip.CreateEntry("ComicInfo.xml");
        using (var entryStream = entry.Open())
        {
            var xml = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <ComicInfo>
                  <Series>Series Name</Series>
                  <Number>{number}</Number>
                  <Writer>Jane Doe</Writer>
                </ComicInfo>
                """;
            var bytes = Encoding.UTF8.GetBytes(xml);
            entryStream.Write(bytes, 0, bytes.Length);
        }

        var pageBytes = CreateJpeg(20, 30);
        for (var i = 1; i <= pageCount; i++)
        {
            var page = zip.CreateEntry($"{i:D3}.jpg");
            using var pageStream = page.Open();
            pageStream.Write(pageBytes, 0, pageBytes.Length);
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
