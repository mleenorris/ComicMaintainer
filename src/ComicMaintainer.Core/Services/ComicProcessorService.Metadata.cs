using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using System.Xml;
using System.Xml.Linq;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers.Zip;

namespace ComicMaintainer.Core.Services;

public partial class ComicProcessorService
{
    /// <summary>
    /// The ComicInfo.xml fields normalize owns. A normalize write changes only these elements.
    /// </summary>
    private const ComicMetadataFieldFlags NormalizedFields =
        ComicMetadataFieldFlags.Series | ComicMetadataFieldFlags.Title | ComicMetadataFieldFlags.Issue;

    public Task<ComicMetadata?> GetMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryResolveAllowedPath(filePath, out var resolvedPath))
                return Task.FromResult<ComicMetadata?>(null);

            return GetMetadataAtResolvedPath(resolvedPath, filePath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error reading metadata from {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return Task.FromResult<ComicMetadata?>(null);
        }
    }

    private Task<ComicMetadata?> GetMetadataAtResolvedPath(string resolvedPath, string fallbackFilePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryResolveAllowedPath(resolvedPath, out var currentPath) ||
                !string.Equals(currentPath, resolvedPath, StringComparison.Ordinal) ||
                !File.Exists(resolvedPath) ||
                !IsComicArchive(resolvedPath))
            {
                return Task.FromResult<ComicMetadata?>(null);
            }

            using var archive = ArchiveFactory.OpenArchive(resolvedPath);

            // Look for ComicInfo.xml
            var comicInfoEntry = FindComicInfoEntry(archive);

            if (comicInfoEntry != null)
            {
                var xmlContent = ReadComicInfoXml(comicInfoEntry);
                return Task.FromResult(ParseComicInfoXml(xmlContent));
            }

            // Fallback: parse from filename
            return Task.FromResult<ComicMetadata?>(new ComicMetadata
            {
                Series = ExtractSeriesFromFilename(fallbackFilePath),
                Issue = IssueNumberParser.Parse(Path.GetFileNameWithoutExtension(fallbackFilePath))
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error reading metadata from {FilePath}", LoggingHelper.SanitizePathForLog(fallbackFilePath));
            return Task.FromResult<ComicMetadata?>(null);
        }
    }

    public Task<SeriesMetadata?> GetSeriesMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryResolveAllowedPath(filePath, out var resolvedPath))
                return Task.FromResult<SeriesMetadata?>(null);

            if (!File.Exists(resolvedPath) || !IsComicArchive(resolvedPath))
                return Task.FromResult<SeriesMetadata?>(null);

            using var archive = ArchiveFactory.OpenArchive(resolvedPath);
            var comicInfoEntry = FindComicInfoEntry(archive);

            if (comicInfoEntry != null)
            {
                var xmlContent = ReadComicInfoXml(comicInfoEntry);
                return Task.FromResult(ParseSeriesMetadataXml(xmlContent));
            }

            return Task.FromResult<SeriesMetadata?>(new SeriesMetadata
            {
                Series = ExtractSeriesFromFilename(filePath),
                Issue = IssueNumberParser.Parse(Path.GetFileNameWithoutExtension(filePath)),
                SeriesGroup = Path.GetFileName(Path.GetDirectoryName(filePath))
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error reading series metadata from {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return Task.FromResult<SeriesMetadata?>(null);
        }
    }

    /// <summary>
    /// Applies the non-empty fields of <paramref name="metadata"/> as a metadata edit; fields it
    /// leaves empty are kept as they are. See <see cref="ApplyMetadataEditAsync"/>.
    /// </summary>
    public async Task<bool> UpdateMetadataAsync(string filePath, ComicMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var result = await ApplyMetadataEditAsync(filePath, metadata, ComicMetadataFields.Supplied(metadata), cancellationToken);
        return result.Succeeded;
    }

    public async Task<MetadataWriteResult> ApplyMetadataEditAsync(
        string filePath,
        ComicMetadata metadata,
        ComicMetadataFieldFlags fields,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        await _processingSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await UpdateMetadataCoreAsync(filePath, metadata, fields, cancellationToken, userEdit: true);
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    public async Task<MetadataWriteResult> WriteMetadataAsync(
        string filePath,
        ComicMetadata metadata,
        ComicMetadataFieldFlags fields,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        await _processingSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await UpdateMetadataCoreAsync(filePath, metadata, fields, cancellationToken);
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    /// <summary>
    /// Merges the <paramref name="fields"/> of <paramref name="metadata"/> into the archive's
    /// existing ComicInfo.xml and rewrites the archive, keeping every other element. The archive
    /// is left untouched when it already holds these values. <paramref name="filePath"/> is the
    /// caller's (logical) path; it keys the file store, while all file I/O goes through its
    /// validated physical path.
    /// </summary>
    private async Task<MetadataWriteResult> UpdateMetadataCoreAsync(
        string filePath,
        ComicMetadata metadata,
        ComicMetadataFieldFlags fields,
        CancellationToken cancellationToken,
        bool userEdit = false)
    {
        try
        {
            if (!TryResolveAllowedPath(filePath, out var physicalPath))
            {
                _logger.LogWarning(
                    "Cannot update metadata: file is outside the allowed directories: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return MetadataWriteResult.Failed(filePath);
            }

            if (!File.Exists(physicalPath))
            {
                _logger.LogWarning(
                    "Cannot update metadata: file does not exist: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return MetadataWriteResult.Failed(filePath);
            }

            if (!IsComicArchive(physicalPath))
            {
                _logger.LogWarning(
                    "Cannot update metadata: file is not a supported comic archive: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return MetadataWriteResult.Failed(filePath);
            }

            if (fields == ComicMetadataFieldFlags.None)
            {
                _logger.LogDebug("No metadata fields to write for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return new MetadataWriteResult(MetadataWriteOutcome.Unchanged, filePath);
            }

            var existing = ReadComicInfoDocument(physicalPath);
            var updated = ComicInfoXml.Merge(existing, metadata, fields);
            if (ComicInfoXml.AreEquivalent(existing, updated) ||
                (existing is null && !updated.Root!.HasElements))
            {
                _logger.LogDebug(
                    "ComicInfo.xml already holds these values, leaving the archive unchanged: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return new MetadataWriteResult(MetadataWriteOutcome.Unchanged, filePath);
            }

            _logger.LogInformation("Updating metadata for: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            var result = await RewriteArchiveAsync(filePath, physicalPath, ComicInfoXml.ToUtf8Bytes(updated), cancellationToken);
            _logger.LogInformation("Successfully updated metadata for: {FilePath}", LoggingHelper.SanitizePathForLog(result.FilePath));
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error updating metadata for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return MetadataWriteResult.Failed(filePath);
        }
    }

    public async Task<bool> RemoveMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await _processingSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await RemoveMetadataCoreAsync(filePath, cancellationToken);
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    private async Task<bool> RemoveMetadataCoreAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryResolveAllowedPath(filePath, out var physicalPath))
            {
                _logger.LogWarning(
                    "Cannot remove metadata: file is outside the allowed directories: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return false;
            }

            if (!File.Exists(physicalPath))
            {
                _logger.LogWarning(
                    "Cannot remove metadata: file does not exist: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return false;
            }

            if (!IsComicArchive(physicalPath))
            {
                _logger.LogWarning(
                    "Cannot remove metadata: file is not a supported comic archive: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return false;
            }

            if (!HasComicInfoEntry(physicalPath))
            {
                _logger.LogInformation("No metadata to remove from: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return true;
            }

            _logger.LogInformation("Removing metadata for: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            var result = await RewriteArchiveAsync(filePath, physicalPath, comicInfoXml: null, cancellationToken);
            _logger.LogInformation("Successfully removed metadata for: {FilePath}", LoggingHelper.SanitizePathForLog(result.FilePath));
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error removing metadata for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return false;
        }
    }

    /// <summary>
    /// Writes the archive at <paramref name="physicalPath"/> again with <paramref name="comicInfoXml"/>
    /// as its ComicInfo.xml (none when null) and puts it in place of the original.
    /// </summary>
    private async Task<MetadataWriteResult> RewriteArchiveAsync(
        string logicalPath,
        string physicalPath,
        byte[]? comicInfoXml,
        CancellationToken cancellationToken)
    {
        // Ensure the temp directory exists
        if (!Directory.Exists(_settings.TempFileDirectory))
        {
            Directory.CreateDirectory(_settings.TempFileDirectory);
            _logger.LogInformation("Created temp directory: {TempDir}", LoggingHelper.SanitizePathForLog(_settings.TempFileDirectory));
        }

        var tempFile = Path.Join(_settings.TempFileDirectory, $".tmp_{Guid.NewGuid()}.cbz");

        try
        {
            // Write the new archive to the temp file; RewriteArchive releases all
            // file handles before returning, ahead of the file replacement.
            RewriteArchive(physicalPath, tempFile, comicInfoXml);

            // Give a brief moment for any file handles to be fully released
            await Task.Delay(FileHandleReleaseDelayMs, cancellationToken);

            // Replace original file with updated one using retry logic
            // to handle transient file locks from file system watchers or antivirus
            await ReplaceFileWithRetryAsync(tempFile, physicalPath, cancellationToken);
            await RefreshStoredFileStatsAsync(logicalPath, cancellationToken);
            return new MetadataWriteResult(MetadataWriteOutcome.Written, logicalPath);
        }
        finally
        {
            // Clean up temp file if it still exists
            if (File.Exists(tempFile))
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Failed to delete temp file: {TempFile}", LoggingHelper.SanitizePathForLog(tempFile));
                }
            }
        }
    }

    /// <summary>
    /// Reads the archive's ComicInfo.xml, or returns null when it has none. Throws
    /// <see cref="InvalidDataException"/> when the document is malformed, so a write never
    /// replaces metadata it could not read.
    /// </summary>
    private static XDocument? ReadComicInfoDocument(string physicalPath)
    {
        using var archive = ArchiveFactory.OpenArchive(physicalPath);
        var entry = FindComicInfoEntry(archive);
        if (entry is null)
        {
            return null;
        }

        var xml = ReadComicInfoXml(entry);
        try
        {
            return ComicInfoXml.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException(
                "The archive's ComicInfo.xml is malformed, so it is left as it is instead of being replaced.", ex);
        }
    }

    private static bool HasComicInfoEntry(string physicalPath)
    {
        using var archive = ArchiveFactory.OpenArchive(physicalPath);
        return FindComicInfoEntry(archive) is not null;
    }

    private static IArchiveEntry? FindComicInfoEntry(IArchive archive) =>
        archive.Entries.FirstOrDefault(e =>
            e.Key?.Equals(ComicInfoXml.EntryName, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>
    /// Writes a zip to <paramref name="destinationPath"/> holding every non-directory entry of
    /// <paramref name="sourcePath"/> except ComicInfo.xml (same order, names and timestamps),
    /// followed by <paramref name="comicInfoXml"/> as ComicInfo.xml when it is not null.
    /// Entries are streamed one at a time, so memory use does not grow with entry or archive size.
    /// Throws if any entry cannot be copied, so callers never replace the original with an
    /// archive that is missing pages.
    /// </summary>
    private static void RewriteArchive(string sourcePath, string destinationPath, byte[]? comicInfoXml)
    {
        using var sourceArchive = ArchiveFactory.OpenArchive(sourcePath);
        using var destination = File.Create(destinationPath);
        using var writer = new ZipWriter(destination, new ZipWriterOptions(CompressionType.Deflate));

        foreach (var entry in sourceArchive.Entries.Where(e => !e.IsDirectory))
        {
            if (entry.Key == null)
            {
                throw new InvalidDataException("The archive contains an entry without a name, which cannot be copied.");
            }

            if (entry.Key.Equals(ComicInfoXml.EntryName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var entryStream = entry.OpenEntryStream();
            writer.Write(entry.Key, entryStream, entry.LastModifiedTime);
        }

        if (comicInfoXml != null)
        {
            using var xmlStream = new MemoryStream(comicInfoXml);
            writer.Write(ComicInfoXml.EntryName, xmlStream, DateTime.Now);
        }
    }

    /// <summary>
    /// Points <paramref name="filePath"/> and <paramref name="resolvedPath"/> at
    /// <paramref name="newFilePath"/>, where a metadata write left the file (a converted archive
    /// becomes a new CBZ). Returns false, leaving both unchanged, when the new path does not
    /// resolve inside the library.
    /// </summary>
    private bool FollowMovedFile(string newFilePath, ref string filePath, ref string resolvedPath)
    {
        if (string.Equals(newFilePath, filePath, StringComparison.Ordinal))
        {
            return true;
        }

        if (!TryResolveAllowedPath(newFilePath, out var movedPath))
        {
            _logger.LogWarning(
                "The rewritten archive {FilePath} does not resolve inside the library",
                LoggingHelper.SanitizePathForLog(newFilePath));
            return false;
        }

        filePath = newFilePath;
        resolvedPath = movedPath;
        return true;
    }

    /// <summary>
    /// Keeps the tracked FileSize/LastModified in sync after an in-place
    /// archive rewrite, so the library scan's modified-file detection
    /// doesn't flag our own writes. Best-effort.
    /// </summary>
    private async Task RefreshStoredFileStatsAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            await _fileStore.RefreshFileStatsAsync(new[] { filePath }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Failed to refresh stored file stats for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
        }
    }
}
