using System.Xml.Serialization;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;

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
    private readonly ComicArchiveReader _archive;
    private bool _disposed;

    public ComicArchive(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Comic file not found: {filePath}");
        }

        _filePath = filePath;
        
        if (!ComicFileExtensions.IsComicArchive(filePath))
        {
            throw new NotSupportedException($"Unsupported comic format: {Path.GetExtension(filePath)}");
        }

        _archive = ComicArchiveReader.Open(filePath);
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
            var comicInfoEntry = _archive.FindComicInfoEntry();

            if (comicInfoEntry == null)
            {
                // No ComicInfo.xml found, return empty tags
                return new ComicInfo();
            }

            // Read and deserialize ComicInfo.xml
            var xmlContent = comicInfoEntry.ReadText();

            var serializer = new XmlSerializer(typeof(ComicInfo));
            using var stringReader = new StringReader(xmlContent);
            return serializer.Deserialize(stringReader) as ComicInfo;
        }
        catch (ArchiveLimitExceededException)
        {
            // Not a damaged file to treat as untagged: the caller decides what a refusal means.
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
