using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml.Serialization;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SystemZipArchive = System.IO.Compression.ZipArchive;
using SixLabors.ImageSharp;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Converts CBZ/CBR archives into fixed-layout EPUB3 files.
/// </summary>
/// <remarks>
/// The generated EPUB keeps the original page images untouched and wraps each of
/// them in a pre-paginated XHTML page whose viewport matches the image size, which
/// is what ereaders (Kindle/Kobo) expect for comics. Metadata is taken from the
/// archive's ComicInfo.xml when present so the book shows up with the right
/// series/issue title on the device.
/// </remarks>
public class EpubConversionService : IEpubConversionService
{
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };

    private readonly ILogger<EpubConversionService> _logger;

    public EpubConversionService(ILogger<EpubConversionService> logger)
    {
        _logger = logger;
    }

    public async Task<string> ConvertToEpubAsync(
        string comicFilePath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(comicFilePath))
        {
            throw new ArgumentException("Comic file path is required", nameof(comicFilePath));
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Output directory is required", nameof(outputDirectory));
        }

        if (!File.Exists(comicFilePath))
        {
            throw new FileNotFoundException($"Comic file not found: {comicFilePath}", comicFilePath);
        }

        if (!ComicFileExtensions.IsComicArchive(comicFilePath))
        {
            throw new NotSupportedException($"Unsupported comic format: {Path.GetExtension(comicFilePath)}");
        }

        return await Task.Run(() => Convert(comicFilePath, outputDirectory, cancellationToken), cancellationToken);
    }

    private string Convert(string comicFilePath, string outputDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);

        using var archive = OpenArchive(comicFilePath);

        var pages = archive.Entries
            .Where(e => !e.IsDirectory && IsImageFile(e.Key))
            .OrderBy(e => e.Key, new NaturalStringComparer())
            .ToList();

        if (pages.Count == 0)
        {
            throw new InvalidOperationException(
                $"Archive contains no page images and cannot be converted to EPUB: {Path.GetFileName(comicFilePath)}");
        }

        var comicInfo = ReadComicInfo(archive);
        var title = BuildTitle(comicInfo, comicFilePath);
        var bookId = "urn:uuid:" + Guid.NewGuid().ToString("D");

        var epubPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(comicFilePath) + ".epub");
        var tempPath = epubPath + ".tmp";

        try
        {
            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var epub = new SystemZipArchive(fileStream, ZipArchiveMode.Create))
            {
                // The mimetype entry must be first and stored uncompressed (OCF spec).
                WriteEntry(epub, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
                WriteEntry(epub, "META-INF/container.xml", ContainerXml);

                var manifestPages = new List<EpubPage>(pages.Count);
                var index = 0;
                foreach (var page in pages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    index++;

                    var extension = NormalizeImageExtension(Path.GetExtension(page.Key) ?? ".jpg");
                    var imageName = $"images/page{index:D4}{extension}";

                    byte[] imageBytes;
                    using (var entryStream = page.OpenEntryStream())
                    using (var buffer = new MemoryStream())
                    {
                        entryStream.CopyTo(buffer);
                        imageBytes = buffer.ToArray();
                    }

                    var (width, height) = GetImageSize(imageBytes, page.Key);

                    var imageEntry = epub.CreateEntry("OEBPS/" + imageName, CompressionLevel.NoCompression);
                    using (var imageStream = imageEntry.Open())
                    {
                        imageStream.Write(imageBytes, 0, imageBytes.Length);
                    }

                    var pageName = $"page{index:D4}.xhtml";
                    WriteEntry(epub, "OEBPS/" + pageName, BuildPageXhtml(index, imageName, width, height));

                    manifestPages.Add(new EpubPage(
                        Id: $"page{index:D4}",
                        XhtmlPath: pageName,
                        ImagePath: imageName,
                        MediaType: GetMediaType(extension),
                        Width: width,
                        Height: height));
                }

                WriteEntry(epub, "OEBPS/content.opf", BuildOpf(bookId, title, comicInfo, manifestPages));
                WriteEntry(epub, "OEBPS/nav.xhtml", BuildNav(title, manifestPages));
            }

            File.Move(tempPath, epubPath, overwrite: true);
            _logger.LogInformation(
                "Converted {FilePath} to EPUB with {PageCount} page(s)",
                LoggingHelper.SanitizePathForLog(comicFilePath),
                pages.Count);

            return epubPath;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static IArchive OpenArchive(string comicFilePath)
    {
        var normalized = ComicFileExtensions.NormalizeExtension(Path.GetExtension(comicFilePath).ToLowerInvariant());
        return normalized switch
        {
            ".cbz" => SharpCompress.Archives.Zip.ZipArchive.Open(comicFilePath),
            ".cbr" => RarArchive.Open(comicFilePath),
            _ => throw new NotSupportedException($"Unsupported comic format: {normalized}")
        };
    }

    private ComicInfo? ReadComicInfo(IArchive archive)
    {
        var entry = archive.Entries.FirstOrDefault(e =>
            !e.IsDirectory &&
            string.Equals(Path.GetFileName(e.Key ?? string.Empty), "ComicInfo.xml", StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            return null;
        }

        try
        {
            using var stream = entry.OpenEntryStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var serializer = new XmlSerializer(typeof(ComicInfo));
            return serializer.Deserialize(reader) as ComicInfo;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "Failed to read ComicInfo.xml while converting to EPUB; continuing without metadata");
            return null;
        }
    }

    private static string BuildTitle(ComicInfo? info, string comicFilePath)
    {
        var series = info?.Series?.Trim();
        var number = info?.Number?.Trim();

        if (!string.IsNullOrEmpty(series) && !string.IsNullOrEmpty(number))
        {
            return $"{series} #{number}";
        }

        if (!string.IsNullOrEmpty(info?.Title))
        {
            return info.Title.Trim();
        }

        if (!string.IsNullOrEmpty(series))
        {
            return series;
        }

        return Path.GetFileNameWithoutExtension(comicFilePath);
    }

    private static (int Width, int Height) GetImageSize(byte[] imageBytes, string? entryKey)
    {
        try
        {
            var info = Image.Identify(imageBytes);
            if (info is not null && info.Width > 0 && info.Height > 0)
            {
                return (info.Width, info.Height);
            }
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            // Fall through to the default viewport below; a wrong viewport still renders,
            // whereas failing the whole conversion for one odd page would not.
            _ = entryKey;
        }

        // Common digital comic page size; used only when the image cannot be identified.
        return (1600, 2400);
    }

    private static string BuildPageXhtml(int index, string imagePath, int width, int height)
    {
        var w = width.ToString(CultureInfo.InvariantCulture);
        var h = height.ToString(CultureInfo.InvariantCulture);

        return $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE html>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
            <head>
              <title>Page {{index.ToString(CultureInfo.InvariantCulture)}}</title>
              <meta name="viewport" content="width={{w}}, height={{h}}"/>
              <style type="text/css">
                html, body { margin: 0; padding: 0; height: 100%; }
                img { display: block; width: 100%; height: 100%; }
              </style>
            </head>
            <body>
              <div><img src="{{imagePath}}" alt="Page {{index.ToString(CultureInfo.InvariantCulture)}}"/></div>
            </body>
            </html>
            """;
    }

    private static string BuildOpf(string bookId, string title, ComicInfo? info, IReadOnlyList<EpubPage> pages)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        builder.AppendLine("""<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid" prefix="rendition: http://www.idpf.org/vocab/rendition/#">""");
        builder.AppendLine("""  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">""");
        builder.AppendLine($"""    <dc:identifier id="bookid">{Escape(bookId)}</dc:identifier>""");
        builder.AppendLine($"""    <dc:title>{Escape(title)}</dc:title>""");
        builder.AppendLine($"""    <dc:language>{Escape(NormalizeLanguage(info?.LanguageISO))}</dc:language>""");

        var creator = FirstNonEmpty(info?.Writer, info?.Penciller);
        if (creator is not null)
        {
            builder.AppendLine($"""    <dc:creator>{Escape(creator)}</dc:creator>""");
        }

        if (!string.IsNullOrWhiteSpace(info?.Publisher))
        {
            builder.AppendLine($"""    <dc:publisher>{Escape(info.Publisher.Trim())}</dc:publisher>""");
        }

        if (!string.IsNullOrWhiteSpace(info?.Summary))
        {
            builder.AppendLine($"""    <dc:description>{Escape(info.Summary.Trim())}</dc:description>""");
        }

        if (!string.IsNullOrWhiteSpace(info?.Series))
        {
            builder.AppendLine($"""    <meta property="belongs-to-collection" id="series">{Escape(info.Series.Trim())}</meta>""");
            builder.AppendLine("""    <meta refines="#series" property="collection-type">series</meta>""");

            if (decimal.TryParse(info.Number, NumberStyles.Number, CultureInfo.InvariantCulture, out var issueNumber))
            {
                builder.AppendLine($"""    <meta refines="#series" property="group-position">{issueNumber.ToString(CultureInfo.InvariantCulture)}</meta>""");
            }
        }

        builder.AppendLine($"""    <meta property="dcterms:modified">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</meta>""");
        builder.AppendLine("""    <meta property="rendition:layout">pre-paginated</meta>""");
        builder.AppendLine("""    <meta property="rendition:spread">auto</meta>""");
        builder.AppendLine("""    <meta name="cover" content="cover-image"/>""");
        builder.AppendLine("""  </metadata>""");

        builder.AppendLine("""  <manifest>""");
        builder.AppendLine("""    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>""");
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var imageProperties = i == 0 ? """ properties="cover-image" """.Trim() : string.Empty;
            var imageId = i == 0 ? "cover-image" : $"img{page.Id}";
            builder.AppendLine($"""    <item id="{imageId}" href="{Escape(page.ImagePath)}" media-type="{page.MediaType}"{(imageProperties.Length > 0 ? " " + imageProperties : string.Empty)}/>""");
            builder.AppendLine($"""    <item id="{page.Id}" href="{Escape(page.XhtmlPath)}" media-type="application/xhtml+xml"/>""");
        }
        builder.AppendLine("""  </manifest>""");

        builder.AppendLine("""  <spine>""");
        foreach (var page in pages)
        {
            builder.AppendLine($"""    <itemref idref="{page.Id}"/>""");
        }
        builder.AppendLine("""  </spine>""");
        builder.AppendLine("""</package>""");

        return builder.ToString();
    }

    private static string BuildNav(string title, IReadOnlyList<EpubPage> pages)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        builder.AppendLine("""<!DOCTYPE html>""");
        builder.AppendLine("""<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">""");
        builder.AppendLine($"""<head><title>{Escape(title)}</title></head>""");
        builder.AppendLine("""<body><nav epub:type="toc" id="toc"><h1>Contents</h1><ol>""");
        for (var i = 0; i < pages.Count; i++)
        {
            builder.AppendLine($"""<li><a href="{Escape(pages[i].XhtmlPath)}">Page {(i + 1).ToString(CultureInfo.InvariantCulture)}</a></li>""");
        }
        builder.AppendLine("""</ol></nav></body></html>""");
        return builder.ToString();
    }

    private const string ContainerXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles>
            <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
          </rootfiles>
        </container>
        """;

    private static void WriteEntry(SystemZipArchive archive, string path, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = archive.CreateEntry(path, level);
        using var stream = entry.Open();
        // EPUB readers expect UTF-8 without a BOM.
        var bytes = new UTF8Encoding(false).GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static bool IsImageFile(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        // Skip macOS resource forks that otherwise show up as duplicate pages.
        if (key.Replace('\\', '/').StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(key).ToLowerInvariant();
        return ImageExtensions.Contains(extension);
    }

    private static string NormalizeImageExtension(string extension)
    {
        var lower = extension.ToLowerInvariant();
        return lower == ".jpeg" ? ".jpg" : lower;
    }

    private static string GetMediaType(string extension) => extension switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };

    private static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "en";
        }

        var trimmed = language.Trim();
        return trimmed.All(c => char.IsLetterOrDigit(c) || c == '-') ? trimmed : "en";
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup only.
        }
    }

    private sealed record EpubPage(
        string Id,
        string XhtmlPath,
        string ImagePath,
        string MediaType,
        int Width,
        int Height);
}
