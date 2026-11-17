using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Notifications;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using SharpCompress.Common;
using SharpCompress.Writers.Zip;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default <see cref="IIssueCoverWriter"/>. The archive is rewritten the way the other archive
/// writers rewrite one: under the file's lock, into a sibling temp file that is flushed and then
/// renamed over the original, with the watcher told the change is the application's own.
/// </summary>
public sealed class IssueCoverWriter : IIssueCoverWriter
{
    private static readonly IReadOnlyDictionary<string, string> ContentTypeToExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpeg"] = ".jpg",
            ["image/png"] = ".png",
            ["image/webp"] = ".webp"
        };

    private readonly IFileOperationLock _fileLock;
    private readonly IFileWatcherService _watcher;
    private readonly IFileCoverCacheService _coverCache;
    private readonly IFileStoreService _fileStore;
    private readonly INotificationPublisher _notifications;
    private readonly ILogger<IssueCoverWriter> _logger;

    public IssueCoverWriter(
        IFileOperationLock fileLock,
        IFileWatcherService watcher,
        IFileCoverCacheService coverCache,
        IFileStoreService fileStore,
        INotificationPublisher notifications,
        ILogger<IssueCoverWriter> logger)
    {
        _fileLock = fileLock;
        _watcher = watcher;
        _coverCache = coverCache;
        _fileStore = fileStore;
        _notifications = notifications;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IssueCoverWriteOutcome> ReplaceFirstPageAsync(
        string archivePath,
        byte[] image,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!ContentTypeToExtension.TryGetValue((contentType ?? string.Empty).Trim(), out var extension) || image.Length == 0)
        {
            return IssueCoverWriteOutcome.UnsupportedImage;
        }

        if (!ComicFileExtensions.IsWritableArchive(archivePath))
        {
            return IssueCoverWriteOutcome.NotWritable;
        }

        IssueCoverWriteOutcome outcome;
        using (await _fileLock.AcquireAsync(archivePath, cancellationToken))
        {
            // The archive may have been renamed or deleted while the lock was awaited.
            if (!File.Exists(archivePath))
            {
                return IssueCoverWriteOutcome.NotFound;
            }

            outcome = RewriteFirstPage(archivePath, image, extension, () => _watcher.SuppressProcessing(archivePath));
        }

        if (outcome == IssueCoverWriteOutcome.Replaced)
        {
            _logger.LogInformation("Replaced the cover of {Path}", LoggingHelper.SanitizePathForLog(archivePath));
            await _coverCache.InvalidateAsync(archivePath, cancellationToken);
            await _fileStore.RefreshFileStatsAsync([archivePath], cancellationToken);
            _notifications.LibraryChanged();
        }

        return outcome;
    }

    /// <summary>
    /// Writes <paramref name="archivePath"/> again with its first page replaced by
    /// <paramref name="image"/>, named like the page it replaces with <paramref name="extension"/>.
    /// Nothing is written when the first page already holds these bytes under that name.
    /// </summary>
    internal static IssueCoverWriteOutcome RewriteFirstPage(
        string archivePath,
        byte[] image,
        string extension,
        Action? onBeforeReplace = null)
    {
        using var source = ComicArchiveReader.Open(archivePath);
        var pages = source.ListPages();
        if (pages.Count == 0)
        {
            return IssueCoverWriteOutcome.NoPages;
        }

        var cover = pages[0];
        var coverName = CoverEntryName(cover.Name, extension, source);
        if (string.Equals(coverName, cover.Name, StringComparison.Ordinal)
            && source.GetEntryAt(cover.Position) is { } entry
            && BytesEqual(entry, image))
        {
            return IssueCoverWriteOutcome.Unchanged;
        }

        var directory = Path.GetDirectoryName(archivePath)
            ?? throw new IOException("Cannot determine the archive's directory.");
        var tempFile = ArchiveWriteRecoveryService.CreateTempPath(directory);
        try
        {
            using (var destination = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new ZipWriter(destination, new ZipWriterOptions(CompressionType.Deflate) { LeaveStreamOpen = true }))
                {
                    using (var coverStream = new MemoryStream(image, writable: false))
                    {
                        writer.Write(coverName, coverStream, DateTime.Now);
                    }

                    source.CopyFileEntriesTo(writer, name => !string.Equals(name, cover.Name, StringComparison.Ordinal));
                }

                // Durable before it is renamed over the original.
                destination.Flush(flushToDisk: true);
            }

            CopyUnixFileMode(archivePath, tempFile);
            source.Dispose();
            onBeforeReplace?.Invoke();
            File.Move(tempFile, archivePath, overwrite: true);
            // The overwrite raises its own file-system events, which may arrive after the first mark.
            onBeforeReplace?.Invoke();
            return IssueCoverWriteOutcome.Replaced;
        }
        catch
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// The new cover's entry name: the old page's name with the new image's extension, so it
    /// sorts where the old one did. When another entry already has that name, "-cover" is added
    /// before the extension, which still sorts first among names that start the same.
    /// </summary>
    private static string CoverEntryName(string pageName, string extension, ComicArchiveReader source)
    {
        var name = Path.ChangeExtension(pageName, extension);
        if (string.Equals(name, pageName, StringComparison.OrdinalIgnoreCase))
        {
            return pageName;
        }

        var taken = source.EnumerateEntries().Any(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
        return taken ? Path.ChangeExtension(pageName, null) + "-cover" + extension : name;
    }

    private static bool BytesEqual(ComicArchiveEntry entry, byte[] expected)
    {
        try
        {
            // A larger entry is refused by its declared size, without being read.
            return entry.ReadAllBytes(Math.Max(expected.Length, 1)).AsSpan().SequenceEqual(expected);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static void CopyUnixFileMode(string sourcePath, string destinationPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(destinationPath, File.GetUnixFileMode(sourcePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The archive keeps the default permissions for new files.
        }
    }
}
