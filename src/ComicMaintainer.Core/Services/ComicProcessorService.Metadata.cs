using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers.Zip;

namespace ComicMaintainer.Core.Services;

public partial class ComicProcessorService
{
    public Task<ComicMetadata?> GetMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryResolveAllowedPath(filePath, out var resolvedPath))
                return Task.FromResult<ComicMetadata?>(null);

            return GetMetadataAtResolvedPath(resolvedPath, filePath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error reading metadata from {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return Task.FromResult<ComicMetadata?>(null);
        }
    }

    private Task<ComicMetadata?> GetMetadataAtResolvedPath(string resolvedPath, string fallbackFilePath)
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
            var comicInfoEntry = archive.Entries.FirstOrDefault(e => 
                e.Key?.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase) == true);

            if (comicInfoEntry != null)
            {
                var xmlContent = ReadComicInfoXml(comicInfoEntry);
                return Task.FromResult(ParseComicInfoXml(xmlContent));
            }

            // Fallback: parse from filename
            return Task.FromResult<ComicMetadata?>(new ComicMetadata
            {
                Series = ExtractSeriesFromFilename(fallbackFilePath),
                Issue = ParseIssueNumber(Path.GetFileNameWithoutExtension(fallbackFilePath))
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
            var comicInfoEntry = archive.Entries.FirstOrDefault(e =>
                e.Key?.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase) == true);

            if (comicInfoEntry != null)
            {
                var xmlContent = ReadComicInfoXml(comicInfoEntry);
                return Task.FromResult(ParseSeriesMetadataXml(xmlContent));
            }

            return Task.FromResult<SeriesMetadata?>(new SeriesMetadata
            {
                Series = ExtractSeriesFromFilename(filePath),
                Issue = ParseIssueNumber(Path.GetFileNameWithoutExtension(filePath)),
                SeriesGroup = Path.GetFileName(Path.GetDirectoryName(filePath))
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error reading series metadata from {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return Task.FromResult<SeriesMetadata?>(null);
        }
    }

    public async Task<bool> UpdateMetadataAsync(string filePath, ComicMetadata metadata, CancellationToken cancellationToken = default)
    {
        await _processingSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await UpdateMetadataCoreAsync(filePath, metadata, cancellationToken);
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    private async Task<bool> UpdateMetadataCoreAsync(string filePath, ComicMetadata metadata, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryResolveAllowedPath(filePath, out var resolvedPath))
            {
                _logger.LogWarning(
                    "Cannot update metadata: file is outside the allowed directories: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return false;
            }

            // Operate on the validated physical path so a linked path component
            // cannot redirect the rewrite to an archive outside the library.
            filePath = resolvedPath;

            if (!File.Exists(filePath))
            {
                _logger.LogWarning(
                    "Cannot update metadata: file does not exist: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return false;
            }

            if (!IsComicArchive(filePath))
            {
                _logger.LogWarning(
                    "Cannot update metadata: file is not a supported comic archive: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return false;
            }

            _logger.LogInformation("Updating metadata for: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            // Create ComicInfo.xml content
            var comicInfoXml = GenerateComicInfoXml(metadata);
            
            // Create temporary file in the configured temp directory
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
                RewriteArchive(filePath, tempFile, comicInfoXml);
                
                // Give a brief moment for any file handles to be fully released
                await Task.Delay(FileHandleReleaseDelayMs, cancellationToken);
                
                // Replace original file with updated one using retry logic
                // to handle transient file locks from file system watchers or antivirus
                await ReplaceFileWithRetryAsync(tempFile, filePath, cancellationToken);
                await RefreshStoredFileStatsAsync(filePath, cancellationToken);
                
                _logger.LogInformation("Successfully updated metadata for: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return true;
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error updating metadata for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return false;
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
            if (!TryResolveAllowedPath(filePath, out var resolvedPath))
            {
                _logger.LogWarning(
                    "Cannot remove metadata: file is outside the allowed directories: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return false;
            }

            // Operate on the validated physical path so a linked path component
            // cannot redirect the rewrite to an archive outside the library.
            filePath = resolvedPath;

            if (!File.Exists(filePath))
            {
                _logger.LogWarning(
                    "Cannot remove metadata: file does not exist: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return false;
            }

            if (!IsComicArchive(filePath))
            {
                _logger.LogWarning(
                    "Cannot remove metadata: file is not a supported comic archive: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return false;
            }

            _logger.LogInformation("Removing metadata for: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            // Ensure the temp directory exists
            if (!Directory.Exists(_settings.TempFileDirectory))
            {
                Directory.CreateDirectory(_settings.TempFileDirectory);
                _logger.LogInformation("Created temp directory: {TempDir}", LoggingHelper.SanitizePathForLog(_settings.TempFileDirectory));
            }

            var tempFile = Path.Join(_settings.TempFileDirectory, $".tmp_{Guid.NewGuid()}.cbz");

            try
            {
                // Rewrite the archive without ComicInfo.xml; RewriteArchive releases all
                // file handles before returning, ahead of File.Replace.
                RewriteArchive(filePath, tempFile, comicInfoXml: null);

                await Task.Delay(FileHandleReleaseDelayMs, cancellationToken);

                await ReplaceFileWithRetryAsync(tempFile, filePath, cancellationToken);
                await RefreshStoredFileStatsAsync(filePath, cancellationToken);

                _logger.LogInformation("Successfully removed metadata for: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return true;
            }
            finally
            {
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error removing metadata for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return false;
        }
    }

    /// <summary>
    /// Writes a zip to <paramref name="destinationPath"/> holding every non-directory entry of
    /// <paramref name="sourcePath"/> except ComicInfo.xml (same order, names and timestamps),
    /// followed by <paramref name="comicInfoXml"/> as ComicInfo.xml when it is not null.
    /// Entries are streamed one at a time, so memory use does not grow with entry or archive size.
    /// Throws if any entry cannot be copied, so callers never replace the original with an
    /// archive that is missing pages.
    /// </summary>
    private static void RewriteArchive(string sourcePath, string destinationPath, string? comicInfoXml)
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

            if (entry.Key.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var entryStream = entry.OpenEntryStream();
            writer.Write(entry.Key, entryStream, entry.LastModifiedTime);
        }

        if (comicInfoXml != null)
        {
            using var xmlStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(comicInfoXml));
            writer.Write("ComicInfo.xml", xmlStream, DateTime.Now);
        }
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
