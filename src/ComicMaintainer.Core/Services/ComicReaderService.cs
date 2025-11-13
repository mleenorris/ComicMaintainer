using System.IO.Compression;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Service for reading and extracting pages from comic archives (CBZ/CBR)
/// </summary>
public class ComicReaderService : IComicReaderService
{
    private readonly ILogger<ComicReaderService> _logger;
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };

    public ComicReaderService(ILogger<ComicReaderService> logger)
    {
        _logger = logger;
    }

    public async Task<int> GetPageCountAsync(string filePath)
    {
        try
        {
            var pages = await GetPageNamesAsync(filePath);
            return pages.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting page count for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return 0;
        }
    }

    public async Task<(byte[] Data, string ContentType)?> GetPageAsync(string filePath, int pageNumber)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("File not found: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return null;
            }

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            
            if (extension == ".cbz")
            {
                return await GetPageFromCbzAsync(filePath, pageNumber);
            }
            else if (extension == ".cbr")
            {
                return await GetPageFromCbrAsync(filePath, pageNumber);
            }
            
            _logger.LogWarning("Unsupported file extension: {Extension}", extension);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting page {PageNumber} from {FilePath}", pageNumber, LoggingHelper.SanitizePathForLog(filePath));
            return null;
        }
    }

    public async Task<List<string>> GetPageNamesAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("File not found: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return new List<string>();
            }

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            
            if (extension == ".cbz")
            {
                return await GetPageNamesFromCbzAsync(filePath);
            }
            else if (extension == ".cbr")
            {
                return await GetPageNamesFromCbrAsync(filePath);
            }
            
            _logger.LogWarning("Unsupported file extension: {Extension}", extension);
            return new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting page names from {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return new List<string>();
        }
    }

    private async Task<List<string>> GetPageNamesFromCbzAsync(string filePath)
    {
        return await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(filePath);
            return archive.Entries
                .Where(e => !e.FullName.EndsWith('/') && IsImageFile(e.FullName))
                .OrderBy(e => e.FullName, new NaturalStringComparer())
                .Select(e => e.FullName)
                .ToList();
        });
    }

    private async Task<List<string>> GetPageNamesFromCbrAsync(string filePath)
    {
        return await Task.Run(() =>
        {
            using var archive = RarArchive.Open(filePath);
            return archive.Entries
                .Where(e => !e.IsDirectory && IsImageFile(e.Key ?? ""))
                .OrderBy(e => e.Key, new NaturalStringComparer())
                .Select(e => e.Key ?? "")
                .ToList();
        });
    }

    private async Task<(byte[] Data, string ContentType)?> GetPageFromCbzAsync(string filePath, int pageNumber)
    {
        return await Task.Run<(byte[] Data, string ContentType)?>(() =>
        {
            using var archive = ZipFile.OpenRead(filePath);
            var imageEntries = archive.Entries
                .Where(e => !e.FullName.EndsWith('/') && IsImageFile(e.FullName))
                .OrderBy(e => e.FullName, new NaturalStringComparer())
                .ToList();

            if (pageNumber < 1 || pageNumber > imageEntries.Count)
            {
                _logger.LogWarning("Page number {PageNumber} out of range (1-{Count})", pageNumber, imageEntries.Count);
                return null;
            }

            var entry = imageEntries[pageNumber - 1];
            using var stream = entry.Open();
            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            
            var contentType = GetContentType(entry.FullName);
            return (memoryStream.ToArray(), contentType);
        });
    }

    private async Task<(byte[] Data, string ContentType)?> GetPageFromCbrAsync(string filePath, int pageNumber)
    {
        return await Task.Run<(byte[] Data, string ContentType)?>(() =>
        {
            using var archive = RarArchive.Open(filePath);
            var imageEntries = archive.Entries
                .Where(e => !e.IsDirectory && IsImageFile(e.Key ?? ""))
                .OrderBy(e => e.Key, new NaturalStringComparer())
                .ToList();

            if (pageNumber < 1 || pageNumber > imageEntries.Count)
            {
                _logger.LogWarning("Page number {PageNumber} out of range (1-{Count})", pageNumber, imageEntries.Count);
                return null;
            }

            var entry = imageEntries[pageNumber - 1];
            using var stream = entry.OpenEntryStream();
            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            
            var contentType = GetContentType(entry.Key ?? "");
            return (memoryStream.ToArray(), contentType);
        });
    }

    private bool IsImageFile(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return ImageExtensions.Contains(extension);
    }

    private string GetContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }
}
