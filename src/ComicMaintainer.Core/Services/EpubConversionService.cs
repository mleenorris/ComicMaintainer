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
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Converts CBZ/CBR archives into fixed-layout EPUB3 files.
/// </summary>
/// <remarks>
/// Page images are only kept byte-for-byte when the source format is already
/// safe for ereaders and no size-budget compression tier applies; otherwise
/// they are re-encoded as JPEG (and downscaled, once the book needs to shrink
/// to fit <see cref="EpubConversionOptions.MaxSizeBytes"/>). Each image is
/// wrapped in a pre-paginated XHTML page whose viewport matches the image
/// size, which is what ereaders (Kindle/Kobo) expect for comics. Metadata is
/// taken from the archive's ComicInfo.xml when present so the book shows up
/// with the right series/issue title on the device.
/// </remarks>
public class EpubConversionService : IEpubConversionService
{
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };

    // Upper bound on decoded pixel count. A small (byte-capped) but pathological
    // archive entry could otherwise declare enormous dimensions and exhaust
    // memory/CPU when fully decoded (a "decompression bomb"). 100 megapixels is
    // far larger than any real comic page yet cheap to reject from the header
    // alone. Mirrors SeriesImageDownscaler.MaxDecodedPixels.
    private const long MaxDecodedPixels = 100L * 1000 * 1000;

    private readonly ILogger<EpubConversionService> _logger;

    public EpubConversionService(ILogger<EpubConversionService> logger)
    {
        _logger = logger;
    }

    public Task<string> ConvertToEpubAsync(
        string comicFilePath,
        string outputDirectory,
        CancellationToken cancellationToken)
        => ConvertToEpubAsync(comicFilePath, outputDirectory, options: null, cancellationToken);

    public async Task<string> ConvertToEpubAsync(
        string comicFilePath,
        string outputDirectory,
        EpubConversionOptions? options = null,
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

        return await Task.Run(() => Convert(comicFilePath, outputDirectory, options, cancellationToken), cancellationToken);
    }

    private string Convert(
        string comicFilePath,
        string outputDirectory,
        EpubConversionOptions? options,
        CancellationToken cancellationToken)
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
        var seriesName = FirstNonEmpty(options?.SeriesTitle, comicInfo?.Series);
        var title = BuildTitle(seriesName, comicInfo, comicFilePath);
        var bookId = "urn:uuid:" + Guid.NewGuid().ToString("D");
        var seriesCover = LoadSeriesCover(options?.SeriesImagePath);

        var epubPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(comicFilePath) + ".epub");
        var tempPath = epubPath + ".tmp";

        // Ereader mailboxes cap attachment size, so an oversized book is rebuilt
        // with progressively stronger page compression instead of failing.
        var tiers = BuildCompressionTiers(options?.MaxSizeBytes);

        try
        {
            for (var tierIndex = 0; tierIndex < tiers.Count; tierIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var tier = tiers[tierIndex];
                var expectedEntries = WriteEpub(
                    tempPath,
                    pages,
                    comicInfo,
                    seriesName,
                    title,
                    bookId,
                    seriesCover,
                    tier,
                    cancellationToken);

                // The archive is complete only after the ZipArchive is disposed;
                // re-read it so a structurally broken book is never handed to callers.
                ValidateEpub(tempPath, expectedEntries, comicFilePath);

                var length = new FileInfo(tempPath).Length;
                var isLastTier = tierIndex == tiers.Count - 1;
                if (!isLastTier && options?.MaxSizeBytes is long budget && length > budget)
                {
                    _logger.LogInformation(
                        "EPUB for {FilePath} is {Bytes} bytes which exceeds the {Budget} byte budget; retrying with stronger page compression",
                        LoggingHelper.SanitizePathForLog(comicFilePath),
                        length,
                        budget);
                    TryDelete(tempPath);
                    continue;
                }

                File.Move(tempPath, epubPath, overwrite: true);
                _logger.LogInformation(
                    "Converted {FilePath} to EPUB with {PageCount} page(s) ({Bytes} bytes, compression tier {Tier})",
                    LoggingHelper.SanitizePathForLog(comicFilePath),
                    pages.Count,
                    length,
                    tierIndex);

                return epubPath;
            }

            // BuildCompressionTiers always yields at least one tier.
            throw new InvalidOperationException(
                $"No EPUB could be produced for {Path.GetFileName(comicFilePath)}.");
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Writes one complete EPUB variant and returns the entries it must contain.
    /// </summary>
    private List<string> WriteEpub(
        string tempPath,
        IReadOnlyList<IArchiveEntry> pages,
        ComicInfo? comicInfo,
        string? seriesName,
        string title,
        string bookId,
        SeriesCover? seriesCover,
        CompressionTier tier,
        CancellationToken cancellationToken)
    {
        var requiredEntries = new List<string> { "META-INF/container.xml" };

        using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var epub = new SystemZipArchive(fileStream, ZipArchiveMode.Create))
        {
            // The mimetype entry must be first and stored uncompressed (OCF spec).
            WriteEntry(epub, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            WriteEntry(epub, "META-INF/container.xml", ContainerXml);

            if (seriesCover is not null)
            {
                var coverEntry = epub.CreateEntry("OEBPS/" + seriesCover.ImagePath, CompressionLevel.NoCompression);
                using (var coverStream = coverEntry.Open())
                {
                    coverStream.Write(seriesCover.Bytes, 0, seriesCover.Bytes.Length);
                }

                WriteEntry(
                    epub,
                    "OEBPS/" + seriesCover.XhtmlPath,
                    BuildCoverXhtml(title, seriesCover.ImagePath, seriesCover.Width, seriesCover.Height));

                requiredEntries.Add("OEBPS/" + seriesCover.ImagePath);
                requiredEntries.Add("OEBPS/" + seriesCover.XhtmlPath);
            }

            var manifestPages = new List<EpubPage>(pages.Count);
            var index = 0;
            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;

                var extension = NormalizeImageExtension(Path.GetExtension(page.Key) ?? ".jpg");

                byte[] imageBytes;
                using (var entryStream = page.OpenEntryStream())
                using (var buffer = new MemoryStream())
                {
                    entryStream.CopyTo(buffer);
                    imageBytes = buffer.ToArray();
                }

                // A page that cannot be decoded (empty, truncated, or not an
                // image at all) would silently render as a blank page on the
                // device, so the whole conversion fails instead.
                var prepared = PrepareImage(imageBytes, extension, page.Key, tier);
                var imageName = $"images/page{index:D4}{prepared.Extension}";

                var imageEntry = epub.CreateEntry("OEBPS/" + imageName, CompressionLevel.NoCompression);
                using (var imageStream = imageEntry.Open())
                {
                    imageStream.Write(prepared.Bytes, 0, prepared.Bytes.Length);
                }

                var pageName = $"page{index:D4}.xhtml";
                WriteEntry(epub, "OEBPS/" + pageName, BuildPageXhtml(index, imageName, prepared.Width, prepared.Height));

                requiredEntries.Add("OEBPS/" + imageName);
                requiredEntries.Add("OEBPS/" + pageName);

                manifestPages.Add(new EpubPage(
                    Id: $"page{index:D4}",
                    XhtmlPath: pageName,
                    ImagePath: imageName,
                    MediaType: prepared.MediaType,
                    Width: prepared.Width,
                    Height: prepared.Height));
            }

            WriteEntry(epub, "OEBPS/content.opf", BuildOpf(bookId, title, seriesName, comicInfo, seriesCover, manifestPages));
            WriteEntry(epub, "OEBPS/nav.xhtml", BuildNav(title, seriesCover, manifestPages));
            requiredEntries.Add("OEBPS/content.opf");
            requiredEntries.Add("OEBPS/nav.xhtml");
        }

        return requiredEntries;
    }

    /// <summary>
    /// Compression variants tried in order. The first keeps the original page
    /// data (best fidelity); the later ones re-encode every page as JPEG at a
    /// lower quality and, eventually, a smaller resolution. Without a size
    /// budget only the lossless variant is ever built.
    /// </summary>
    private static IReadOnlyList<CompressionTier> BuildCompressionTiers(long? maxSizeBytes)
    {
        if (maxSizeBytes is null or <= 0)
        {
            return new[] { CompressionTier.Lossless };
        }

        return new[]
        {
            CompressionTier.Lossless,
            new CompressionTier(Quality: 80, MaxDimension: 2400),
            new CompressionTier(Quality: 65, MaxDimension: 1800),
            new CompressionTier(Quality: 50, MaxDimension: 1400)
        };
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

    private static string BuildTitle(string? seriesName, ComicInfo? info, string comicFilePath)
    {
        var series = seriesName?.Trim();
        var number = FormatIssueNumberForTitle(info?.Number);

        if (!string.IsNullOrEmpty(series) && number is not null)
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

    /// <summary>
    /// Parses a ComicInfo issue number into a sortable value. Accepts the
    /// decorations that show up in the wild ("#12", "12.", "007") and returns
    /// null for purely alphabetic numbers such as "Annual".
    /// </summary>
    private static decimal? TryParseIssueNumber(string? number)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            return null;
        }

        var trimmed = number.Trim().TrimStart('#', ' ').Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        // Keep only the leading numeric run so "12a" / "12 (of 20)" still sort.
        var end = 0;
        var seenDot = false;
        while (end < trimmed.Length)
        {
            var c = trimmed[end];
            if (char.IsAsciiDigit(c))
            {
                end++;
                continue;
            }

            if (c == '.' && !seenDot && end + 1 < trimmed.Length && char.IsAsciiDigit(trimmed[end + 1]))
            {
                seenDot = true;
                end++;
                continue;
            }

            break;
        }

        if (end == 0)
        {
            return null;
        }

        return decimal.TryParse(
            trimmed[..end],
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Renders the issue number the way ereaders need it in the title. Kindle
    /// and most sideload-friendly readers have no series support and sort
    /// library entries lexicographically by title, so a numeric issue number is
    /// zero-padded ("#010" sorts after "#009", "#10" would not). Non-numeric
    /// numbers ("Annual") are kept verbatim.
    /// </summary>
    private static string? FormatIssueNumberForTitle(string? number)
    {
        var trimmed = number?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var parsed = TryParseIssueNumber(trimmed);
        if (parsed is null)
        {
            return trimmed;
        }

        var whole = decimal.Truncate(Math.Abs(parsed.Value));
        var fraction = Math.Abs(parsed.Value) - whole;
        var padded = whole.ToString("000", CultureInfo.InvariantCulture);

        if (fraction != 0m)
        {
            padded += fraction
                .ToString("0.###", CultureInfo.InvariantCulture)
                .TrimStart('0');
        }

        return parsed.Value < 0 ? "-" + padded : padded;
    }

    /// <summary>
    /// Reads the cached series cover so it can be embedded as the book cover.
    /// Any problem (missing file, unreadable, undecodable) degrades silently to
    /// "no series cover" because the first page is still a usable cover.
    /// </summary>
    private SeriesCover? LoadSeriesCover(string? seriesImagePath)
    {
        if (string.IsNullOrWhiteSpace(seriesImagePath))
        {
            return null;
        }

        try
        {
            if (!File.Exists(seriesImagePath))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(seriesImagePath);
            if (bytes.Length == 0)
            {
                return null;
            }

            // Image.Load fully decodes the pixel data rather than just reading the
            // header (as Image.Identify does), so a truncated/corrupt cached cover
            // reliably throws here and falls back to the first-page cover below.
            using var image = Image.Load(bytes);
            if (image.Width <= 0 || image.Height <= 0)
            {
                return null;
            }

            var extension = NormalizeImageExtension(image.Metadata.DecodedImageFormat?.FileExtensions.FirstOrDefault() is { } ext
                ? "." + ext
                : Path.GetExtension(seriesImagePath));
            var mediaType = GetMediaType(extension);
            if (!IsEreaderSafeMediaType(mediaType))
            {
                // Same reasoning as page images: an unsupported format would show
                // up as a blank cover, so re-encode it as JPEG.
                var encoded = EncodeAsJpeg(image, CompressionTier.Lossless);
                bytes = encoded.Bytes;
                extension = encoded.Extension;
                mediaType = encoded.MediaType;
            }

            return new SeriesCover(
                ImagePath: "images/cover" + extension,
                XhtmlPath: "cover.xhtml",
                MediaType: mediaType,
                Width: image.Width,
                Height: image.Height,
                Bytes: bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnknownImageFormatException or InvalidImageContentException or NotSupportedException or ImageFormatException)
        {
            _logger.LogWarning(
                ex,
                "Could not embed series cover {ImagePath} into EPUB; falling back to the first page",
                LoggingHelper.SanitizePathForLog(seriesImagePath));
            return null;
        }
    }

    /// <summary>
    /// Fully decodes a page image so a corrupt/truncated entry fails the
    /// conversion instead of shipping a blank page, determines the real format
    /// from the bytes (archive file names routinely lie about it, and a wrong
    /// media type also renders blank) and re-encodes formats that ereaders do
    /// not support as JPEG.
    /// </summary>
    private static PreparedImage PrepareImage(
        byte[] imageBytes,
        string fallbackExtension,
        string? entryKey,
        CompressionTier tier)
    {
        if (imageBytes.Length == 0)
        {
            throw new InvalidOperationException(
                $"Page image '{entryKey}' is empty and would render as a blank page.");
        }

        try
        {
            // Cheaply inspect the header dimensions before a full decode so a
            // tiny archive entry that declares an enormous size (a
            // "decompression bomb") is rejected without allocating its pixel
            // buffer.
            var info = Image.Identify(imageBytes);
            if (info is not null && (long)info.Width * info.Height > MaxDecodedPixels)
            {
                throw new InvalidOperationException(
                    $"Page image '{entryKey}' dimensions ({info.Width}x{info.Height}) exceed the decode limit.");
            }

            using var image = Image.Load(imageBytes);
            if (image.Width <= 0 || image.Height <= 0)
            {
                throw new InvalidOperationException(
                    $"Page image '{entryKey}' has no usable dimensions.");
            }

            var extension = NormalizeImageExtension(
                image.Metadata.DecodedImageFormat?.FileExtensions.FirstOrDefault() is { } detected
                    ? "." + detected
                    : fallbackExtension);
            var mediaType = GetMediaType(extension);

            if (tier.IsLossless && IsEreaderSafeMediaType(mediaType))
            {
                return new PreparedImage(imageBytes, extension, mediaType, image.Width, image.Height);
            }

            // Re-encoded either because the source format shows up as a blank
            // page on most ereaders (WebP/BMP) or because the book has to shrink
            // to fit the delivery size budget.
            return EncodeAsJpeg(image, tier);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException or ImageFormatException)
        {
            throw new InvalidOperationException(
                $"Page image '{entryKey}' could not be decoded and would render as a blank page.",
                ex);
        }
    }

    /// <summary>
    /// Re-encodes a decoded image as JPEG, downscaling it first when the tier
    /// caps the longest edge. JPEG is the only format every ereader renders
    /// reliably and it is what keeps an oversized book under the mail limit.
    /// </summary>
    private static PreparedImage EncodeAsJpeg(Image image, CompressionTier tier)
    {
        var width = image.Width;
        var height = image.Height;

        if (tier.MaxDimension is int max && max > 0 && Math.Max(width, height) > max)
        {
            var scale = (double)max / Math.Max(width, height);
            width = Math.Max(1, (int)Math.Round(width * scale));
            height = Math.Max(1, (int)Math.Round(height * scale));
            image.Mutate(ctx => ctx.Resize(width, height));
        }

        using var buffer = new MemoryStream();
        image.Save(buffer, new JpegEncoder { Quality = tier.Quality ?? 90 });
        return new PreparedImage(buffer.ToArray(), ".jpg", "image/jpeg", width, height);
    }

    private static bool IsEreaderSafeMediaType(string mediaType)
        => mediaType is "image/jpeg" or "image/png" or "image/gif";

    /// <summary>
    /// Re-reads the finished EPUB and verifies it is a readable OCF container
    /// whose declared resources all exist and are non-empty. Callers (email
    /// delivery in particular) must never ship a book that fails this check.
    /// </summary>
    private static void ValidateEpub(string epubPath, IReadOnlyList<string> expectedEntries, string comicFilePath)
    {
        var name = Path.GetFileName(comicFilePath);

        try
        {
            using var stream = new FileStream(epubPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new SystemZipArchive(stream, ZipArchiveMode.Read);

            if (archive.Entries.Count == 0 ||
                !string.Equals(archive.Entries[0].FullName, "mimetype", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Generated EPUB for {name} is missing the leading mimetype entry.");
            }

            foreach (var expected in expectedEntries)
            {
                var entry = archive.GetEntry(expected);
                if (entry is null)
                {
                    throw new InvalidOperationException(
                        $"Generated EPUB for {name} is missing required entry '{expected}'.");
                }

                if (entry.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Generated EPUB for {name} contains an empty entry '{expected}'.");
                }
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException($"Generated EPUB for {name} is not a readable archive.", ex);
        }
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

    private static string BuildCoverXhtml(string title, string imagePath, int width, int height)
    {
        var w = width.ToString(CultureInfo.InvariantCulture);
        var h = height.ToString(CultureInfo.InvariantCulture);

        return $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE html>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
            <head>
              <title>{{Escape(title)}}</title>
              <meta name="viewport" content="width={{w}}, height={{h}}"/>
              <style type="text/css">
                html, body { margin: 0; padding: 0; height: 100%; background-color: #000000; }
                img { display: block; width: 100%; height: 100%; }
              </style>
            </head>
            <body epub:type="cover">
              <div><img src="{{imagePath}}" alt="{{Escape(title)}}"/></div>
            </body>
            </html>
            """;
    }

    private static string BuildOpf(
        string bookId,
        string title,
        string? seriesName,
        ComicInfo? info,
        SeriesCover? cover,
        IReadOnlyList<EpubPage> pages)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        builder.AppendLine("""<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid" prefix="rendition: http://www.idpf.org/vocab/rendition/#">""");
        builder.AppendLine("""  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">""");
        builder.AppendLine($"""    <dc:identifier id="bookid">{Escape(bookId)}</dc:identifier>""");
        builder.AppendLine($"""    <dc:title id="title">{Escape(title)}</dc:title>""");
        builder.AppendLine("""    <meta refines="#title" property="title-type">main</meta>""");
        // The title is already zero-padded, so reusing it as the sort key keeps
        // readers that sort by "file-as" consistent with those that sort by title.
        builder.AppendLine($"""    <meta refines="#title" property="file-as">{Escape(title)}</meta>""");
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

        var issueNumber = TryParseIssueNumber(info?.Number);

        if (!string.IsNullOrWhiteSpace(seriesName))
        {
            var series = seriesName.Trim();

            // EPUB3 collection metadata: read by Kobo, Calibre, Kavita, ...
            builder.AppendLine($"""    <meta property="belongs-to-collection" id="series">{Escape(series)}</meta>""");
            builder.AppendLine("""    <meta refines="#series" property="collection-type">series</meta>""");

            if (issueNumber.HasValue)
            {
                builder.AppendLine($"""    <meta refines="#series" property="group-position">{FormatNumber(issueNumber.Value)}</meta>""");
            }

            // Legacy calibre-style series metadata, still the only form many
            // readers and library managers understand.
            builder.AppendLine($"""    <meta name="calibre:series" content="{Escape(series)}"/>""");
            if (issueNumber.HasValue)
            {
                builder.AppendLine($"""    <meta name="calibre:series_index" content="{FormatNumber(issueNumber.Value)}"/>""");
            }
        }

        builder.AppendLine($"""    <meta property="dcterms:modified">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</meta>""");
        builder.AppendLine("""    <meta property="rendition:layout">pre-paginated</meta>""");
        builder.AppendLine("""    <meta property="rendition:spread">auto</meta>""");
        builder.AppendLine("""    <meta name="cover" content="cover-image"/>""");
        builder.AppendLine("""  </metadata>""");

        builder.AppendLine("""  <manifest>""");
        builder.AppendLine("""    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>""");
        if (cover is not null)
        {
            builder.AppendLine($"""    <item id="cover-image" href="{Escape(cover.ImagePath)}" media-type="{cover.MediaType}" properties="cover-image"/>""");
            builder.AppendLine($"""    <item id="cover-page" href="{Escape(cover.XhtmlPath)}" media-type="application/xhtml+xml"/>""");
        }
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            // Without a series cover the first page doubles as the cover image.
            var isCoverImage = cover is null && i == 0;
            var imageId = isCoverImage ? "cover-image" : $"img{page.Id}";
            var imageProperties = isCoverImage ? """ properties="cover-image" """.TrimEnd() : string.Empty;
            builder.AppendLine($"""    <item id="{imageId}" href="{Escape(page.ImagePath)}" media-type="{page.MediaType}"{imageProperties}/>""");
            builder.AppendLine($"""    <item id="{page.Id}" href="{Escape(page.XhtmlPath)}" media-type="application/xhtml+xml"/>""");
        }
        builder.AppendLine("""  </manifest>""");

        builder.AppendLine("""  <spine>""");
        if (cover is not null)
        {
            builder.AppendLine("""    <itemref idref="cover-page" properties="rendition:page-spread-center"/>""");
        }
        foreach (var page in pages)
        {
            builder.AppendLine($"""    <itemref idref="{page.Id}"/>""");
        }
        builder.AppendLine("""  </spine>""");
        builder.AppendLine("""</package>""");

        return builder.ToString();
    }

    private static string FormatNumber(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string BuildNav(string title, SeriesCover? cover, IReadOnlyList<EpubPage> pages)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        builder.AppendLine("""<!DOCTYPE html>""");
        builder.AppendLine("""<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">""");
        builder.AppendLine($"""<head><title>{Escape(title)}</title></head>""");
        builder.AppendLine("""<body><nav epub:type="toc" id="toc"><h1>Contents</h1><ol>""");
        if (cover is not null)
        {
            builder.AppendLine($"""<li><a href="{Escape(cover.XhtmlPath)}">Cover</a></li>""");
        }
        for (var i = 0; i < pages.Count; i++)
        {
            builder.AppendLine($"""<li><a href="{Escape(pages[i].XhtmlPath)}">Page {(i + 1).ToString(CultureInfo.InvariantCulture)}</a></li>""");
        }
        builder.AppendLine("""</ol></nav>""");
        if (cover is not null)
        {
            builder.AppendLine($"""<nav epub:type="landmarks" id="landmarks" hidden="hidden"><ol><li><a epub:type="cover" href="{Escape(cover.XhtmlPath)}">Cover</a></li></ol></nav>""");
        }
        builder.AppendLine("""</body></html>""");
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

    /// <summary>
    /// One page-compression variant. <see cref="Lossless"/> keeps the original
    /// page bytes whenever their format is ereader-safe.
    /// </summary>
    private sealed record CompressionTier(int? Quality = null, int? MaxDimension = null)
    {
        public static readonly CompressionTier Lossless = new();

        public bool IsLossless => Quality is null && MaxDimension is null;
    }

    private sealed record PreparedImage(
        byte[] Bytes,
        string Extension,
        string MediaType,
        int Width,
        int Height);

    private sealed record SeriesCover(
        string ImagePath,
        string XhtmlPath,
        string MediaType,
        int Width,
        int Height,
        byte[] Bytes);
}
