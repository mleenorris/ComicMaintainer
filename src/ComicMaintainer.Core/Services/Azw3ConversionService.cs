using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services.Kf8;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Converts CBZ/CBR archives into fixed-layout AZW3 (Kindle Format 8) books.
/// </summary>
/// <remarks>
/// The pages are prepared by <see cref="IEpubConversionService"/> first: that
/// service already decodes, validates, re-encodes and (when a size budget
/// applies) downscales every page, and resolves the book metadata. This service
/// then re-reads that intermediate EPUB and rewrites it as a KF8 book, so both
/// formats always contain exactly the same pages.
/// </remarks>
public partial class Azw3ConversionService : IAzw3ConversionService
{
    private static readonly XNamespace OpfNamespace = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace DcNamespace = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace ContainerNamespace = "urn:oasis:names:tc:opendocument:xmlns:container";

    private readonly IEpubConversionService _epubConverter;
    private readonly ILogger<Azw3ConversionService> _logger;

    public Azw3ConversionService(
        IEpubConversionService epubConverter,
        ILogger<Azw3ConversionService> logger)
    {
        _epubConverter = epubConverter;
        _logger = logger;
    }

    public Task<string> ConvertToAzw3Async(
        string comicFilePath,
        string outputDirectory,
        EpubConversionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(comicFilePath))
        {
            throw new ArgumentException("Comic file path is required", nameof(comicFilePath));
        }

        return ConvertToAzw3Async(new[] { comicFilePath }, outputDirectory, options, cancellationToken);
    }

    public async Task<string> ConvertToAzw3Async(
        IReadOnlyList<string> comicFilePaths,
        string outputDirectory,
        EpubConversionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comicFilePaths);

        if (comicFilePaths.Count == 0)
        {
            throw new ArgumentException("At least one comic file path is required", nameof(comicFilePaths));
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Output directory is required", nameof(outputDirectory));
        }

        Directory.CreateDirectory(outputDirectory);

        // The intermediate EPUB is an implementation detail: it is built inside
        // the output directory (so it shares its lifetime and permissions) and
        // removed again as soon as the AZW3 has been written.
        var stagingDirectory = Path.Combine(outputDirectory, ".azw3-" + Guid.NewGuid().ToString("N"));

        try
        {
            var epubPath = await _epubConverter.ConvertToEpubAsync(
                comicFilePaths,
                stagingDirectory,
                options,
                cancellationToken);

            var azw3Path = Path.Combine(
                outputDirectory,
                Path.GetFileNameWithoutExtension(epubPath) + ".azw3");
            var tempPath = azw3Path + ".tmp";

            try
            {
                await Task.Run(() => Convert(epubPath, tempPath, cancellationToken), cancellationToken);
                File.Move(tempPath, azw3Path, overwrite: true);
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }

            _logger.LogInformation(
                "Converted {IssueCount} issue(s) starting at {FilePath} to AZW3 ({Bytes} bytes)",
                comicFilePaths.Count,
                LoggingHelper.SanitizePathForLog(comicFilePaths[0]),
                new FileInfo(azw3Path).Length);

            return azw3Path;
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static void Convert(string epubPath, string azw3Path, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(epubPath);

        var opfPath = ReadOpfPath(archive);
        var opf = XDocument.Parse(ReadEntryText(archive, opfPath));
        var opfDirectory = GetDirectory(opfPath);

        var manifest = opf.Root?
            .Element(OpfNamespace + "manifest")?
            .Elements(OpfNamespace + "item")
            .Where(item => item.Attribute("id") is not null && item.Attribute("href") is not null)
            .ToDictionary(
                item => item.Attribute("id")!.Value,
                item => (Href: item.Attribute("href")!.Value, MediaType: item.Attribute("media-type")?.Value ?? string.Empty),
                StringComparer.Ordinal)
            ?? new Dictionary<string, (string Href, string MediaType)>(StringComparer.Ordinal);

        var pages = new List<Kf8Page>();
        var spine = opf.Root?.Element(OpfNamespace + "spine")?.Elements(OpfNamespace + "itemref") ?? [];

        foreach (var itemref in spine)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var idref = itemref.Attribute("idref")?.Value;
            if (idref is null || !manifest.TryGetValue(idref, out var document))
            {
                continue;
            }

            var documentPath = ResolvePath(opfDirectory, document.Href);
            var xhtml = ReadEntryText(archive, documentPath);

            var source = ImageSourceRegex().Match(xhtml);
            if (!source.Success)
            {
                continue;
            }

            var imagePath = ResolvePath(GetDirectory(documentPath), source.Groups[1].Value);
            var imageBytes = ReadEntryBytes(archive, imagePath);

            var info = Image.Identify(imageBytes);
            var mediaType = manifest.Values
                .FirstOrDefault(item => string.Equals(ResolvePath(opfDirectory, item.Href), imagePath, StringComparison.Ordinal))
                .MediaType;

            var title = TitleRegex().Match(xhtml) is { Success: true } match
                ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value)
                : $"Page {pages.Count + 1}";

            pages.Add(new Kf8Page(
                imageBytes,
                string.IsNullOrWhiteSpace(mediaType) ? "image/jpeg" : mediaType,
                info.Width,
                info.Height,
                title));
        }

        if (pages.Count == 0)
        {
            throw new InvalidOperationException(
                $"No pages could be read back from the intermediate book for {Path.GetFileName(azw3Path)}.");
        }

        var metadata = opf.Root?.Element(OpfNamespace + "metadata");
        var book = new Kf8Book(
            Title: metadata?.Element(DcNamespace + "title")?.Value ?? Path.GetFileNameWithoutExtension(azw3Path),
            Pages: pages,
            Creator: metadata?.Element(DcNamespace + "creator")?.Value,
            Publisher: metadata?.Element(DcNamespace + "publisher")?.Value,
            Language: metadata?.Element(DcNamespace + "language")?.Value);

        Kf8ComicWriter.Write(book, azw3Path);
    }

    private static string ReadOpfPath(ZipArchive archive)
    {
        var container = XDocument.Parse(ReadEntryText(archive, "META-INF/container.xml"));
        var fullPath = container.Root?
            .Element(ContainerNamespace + "rootfiles")?
            .Elements(ContainerNamespace + "rootfile")
            .Select(rootfile => rootfile.Attribute("full-path")?.Value)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

        return fullPath ?? throw new InvalidOperationException(
            "The intermediate book has no package document and cannot be converted to AZW3.");
    }

    private static string ReadEntryText(ZipArchive archive, string entryName)
    {
        using var stream = OpenEntry(archive, entryName);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static byte[] ReadEntryBytes(ZipArchive archive, string entryName)
    {
        using var stream = OpenEntry(archive, entryName);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static Stream OpenEntry(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName)
            ?? throw new InvalidOperationException(
                $"The intermediate book is missing the entry '{entryName}' and cannot be converted to AZW3.");

        return entry.Open();
    }

    private static string GetDirectory(string entryName)
    {
        var index = entryName.LastIndexOf('/');
        return index < 0 ? string.Empty : entryName[..(index + 1)];
    }

    /// <summary>
    /// Resolves a relative href from inside the container against the directory
    /// holding the referencing document. The intermediate book is generated by
    /// this application, so the hrefs never escape the container, but "." and
    /// ".." segments are still collapsed rather than trusted.
    /// </summary>
    private static string ResolvePath(string directory, string href)
    {
        var segments = new List<string>();
        foreach (var segment in (directory + href).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to clean up the intermediate book directory");
        }
    }

    private void TryDeleteFile(string path)
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
            _logger.LogWarning(ex, "Failed to clean up the incomplete AZW3 file");
        }
    }

    [GeneratedRegex("<img[^>]*\\ssrc=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ImageSourceRegex();

    [GeneratedRegex("<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();
}
