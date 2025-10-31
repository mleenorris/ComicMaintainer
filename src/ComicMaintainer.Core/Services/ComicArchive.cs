using System.Text;
using System.Xml;
using System.Xml.Serialization;
using ComicMaintainer.Core.Models;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Handles reading and writing comic archives (CBZ/CBR)
/// This is the C# equivalent of Python's comicapi.comicarchive.ComicArchive
/// </summary>
public class ComicArchive : IDisposable
{
    private readonly string _filePath;
    private IArchive? _archive;
    private bool _disposed;

    public ComicArchive(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Comic file not found: {filePath}");
        }

        _filePath = filePath;
        
        // Determine archive type and open
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (extension == ".cbz")
        {
            _archive = ZipArchive.Open(filePath);
        }
        else if (extension == ".cbr")
        {
            _archive = RarArchive.Open(filePath);
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
        if (_archive == null)
        {
            throw new InvalidOperationException("Archive not opened");
        }

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
        catch (Exception)
        {
            // If we can't read the tags, return empty
            return new ComicInfo();
        }
    }

    /// <summary>
    /// Write tags to the comic archive
    /// Compatible with Python ComicTagger's write_tags method
    /// </summary>
    /// <param name="tags">ComicInfo tags to write</param>
    /// <param name="format">Format hint ('cr' for ComicRack/ComicInfo.xml)</param>
    public void WriteTags(ComicInfo tags, string format = "cr")
    {
        if (_archive == null)
        {
            throw new InvalidOperationException("Archive not opened");
        }

        // Only support CBZ for writing (ZIP format)
        var extension = Path.GetExtension(_filePath).ToLowerInvariant();
        if (extension != ".cbz")
        {
            throw new NotSupportedException("Writing tags is only supported for CBZ files");
        }

        try
        {
            // Serialize tags to XML
            var serializer = new XmlSerializer(typeof(ComicInfo));
            var xmlContent = SerializeToXml(tags);

            // Create a temporary file for the new archive
            var tempFile = Path.GetTempFileName();

            try
            {
                // Create new archive with updated ComicInfo.xml
                using (var newArchive = ZipArchive.Create())
                {
                    // Add ComicInfo.xml
                    var comicInfoBytes = Encoding.UTF8.GetBytes(xmlContent);
                    using (var memoryStream = new MemoryStream(comicInfoBytes))
                    {
                        newArchive.AddEntry("ComicInfo.xml", memoryStream, true, comicInfoBytes.Length, DateTime.Now);
                    }

                    // Copy all other entries from original archive
                    foreach (var entry in _archive.Entries.Where(e => !e.IsDirectory))
                    {
                        // Skip existing ComicInfo.xml
                        if (entry.Key?.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            continue;
                        }

                        using var entryStream = entry.OpenEntryStream();
                        using var memStream = new MemoryStream();
                        entryStream.CopyTo(memStream);
                        memStream.Position = 0;
                        
                        newArchive.AddEntry(entry.Key ?? "unknown", memStream, true, memStream.Length, entry.LastModifiedTime ?? DateTime.Now);
                    }

                    // Save to temp file
                    using var fileStream = File.OpenWrite(tempFile);
                    newArchive.SaveTo(fileStream, new WriterOptions(CompressionType.Deflate)
                    {
                        LeaveStreamOpen = false
                    });
                }

                // Close current archive
                _archive?.Dispose();
                _archive = null;

                // Replace original file with new one
                File.Delete(_filePath);
                File.Move(tempFile, _filePath);

                // Reopen archive
                _archive = ZipArchive.Open(_filePath);
            }
            finally
            {
                // Clean up temp file if it still exists
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { /* ignore */ }
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to write tags to archive: {ex.Message}", ex);
        }
    }

    private static string SerializeToXml(ComicInfo tags)
    {
        var serializer = new XmlSerializer(typeof(ComicInfo));
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = false
        };

        using var stringWriter = new StringWriter();
        using var xmlWriter = XmlWriter.Create(stringWriter, settings);
        serializer.Serialize(xmlWriter, tags);
        return stringWriter.ToString();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _archive?.Dispose();
            _disposed = true;
        }
    }
}
