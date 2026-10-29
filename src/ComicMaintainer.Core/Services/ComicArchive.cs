using System.Text;
using System.Xml.Serialization;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Archives.Rar;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Reads the ComicInfo.xml tags of a comic archive (CBZ/CBR).
/// This is the C# equivalent of the reading half of Python's comicapi.comicarchive.ComicArchive.
/// Tags are written by <see cref="ComicProcessorService"/>, which streams the archive's
/// entries into the rewritten file instead of buffering them in memory.
/// </summary>
public class ComicArchive : IDisposable
{
    private readonly string _filePath;
    private readonly IArchive _archive;
    private bool _disposed;

    public ComicArchive(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Comic file not found: {filePath}");
        }

        _filePath = filePath;
        
        // Validate and determine archive type
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (!ComicFileExtensions.IsComicArchive(filePath))
        {
            throw new NotSupportedException($"Unsupported comic format: {extension}");
        }
        
        // Open the appropriate archive type
        var normalizedExt = ComicFileExtensions.NormalizeExtension(extension);
        if (normalizedExt == ".cbz")
        {
            _archive = ZipArchive.OpenArchive(filePath);
        }
        else if (normalizedExt == ".cbr")
        {
            _archive = RarArchive.OpenArchive(filePath);
        }
        else
        {
            throw new NotSupportedException($"Unsupported comic format: {extension}");
        }
    }

    /// <summary>
    /// Read tags from the comic archive
    /// Compatible with Python ComicTagger's read_tags method
    /// </summary>
    /// <param name="format">Format hint ('cr' for ComicRack/ComicInfo.xml)</param>
    public ComicInfo? ReadTags(string format = "cr")
    {
        try
        {
            // Look for ComicInfo.xml in the archive
            var comicInfoEntry = _archive.Entries
                .FirstOrDefault(e => e.Key?.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase) == true);

            if (comicInfoEntry == null)
            {
                // No ComicInfo.xml found, return empty tags
                return new ComicInfo();
            }

            // Read and deserialize ComicInfo.xml
            using var stream = comicInfoEntry.OpenEntryStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var xmlContent = reader.ReadToEnd();

            var serializer = new XmlSerializer(typeof(ComicInfo));
            using var stringReader = new StringReader(xmlContent);
            return serializer.Deserialize(stringReader) as ComicInfo;
        }
        catch (Exception ex)
        {
            // If we can't read the tags, return empty
            // This can happen with corrupted files or invalid XML
            System.Diagnostics.Debug.WriteLine($"Failed to read tags from {_filePath}: {ex.Message}");
            return new ComicInfo();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _archive.Dispose();
            _disposed = true;
        }
    }
}
