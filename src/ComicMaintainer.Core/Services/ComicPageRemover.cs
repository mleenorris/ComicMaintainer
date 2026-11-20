using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Notifications;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using SharpCompress.Common;
using SharpCompress.Writers.Zip;

namespace ComicMaintainer.Core.Services;

/// <summary>What removing pages from a comic did.</summary>
/// <param name="Succeeded">True when the pages were removed.</param>
/// <param name="Removed">How many pages were removed.</param>
/// <param name="PagesLeft">How many pages the comic has now.</param>
/// <param name="Error">Why nothing was removed, safe to show; null on success.</param>
public sealed record PageRemovalResult(bool Succeeded, int Removed, int PagesLeft, string? Error)
{
    internal static PageRemovalResult Refused(string error) => new(false, 0, 0, error);
}

/// <summary>Removes pages (scan credits, ads, a duplicated cover) from a comic's CBZ.</summary>
public interface IComicPageRemover
{
    /// <summary>
    /// Writes the CBZ at <paramref name="filePath"/> again without the pages named
    /// <paramref name="pageNames"/> (entry names, as <c>GET /api/comicreader/pages</c> lists them).
    /// ComicInfo.xml's <c>&lt;Pages&gt;</c> list and <c>&lt;PageCount&gt;</c> follow, so the page
    /// types (FrontCover, …) stay on the pages they described. Nothing is written when a name is
    /// not a page of the archive, or every page would go.
    /// </summary>
    Task<PageRemovalResult> RemovePagesAsync(string filePath, IReadOnlyCollection<string> pageNames, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IComicPageRemover"/>. The archive is rewritten the way the other archive
/// writers rewrite one (see <see cref="IssueCoverWriter"/>): under the file's lock, into a
/// sibling temp file that is flushed and then renamed over the original, with the watcher told
/// the change is the application's own.
/// </summary>
public sealed class ComicPageRemover : IComicPageRemover
{
    private readonly IFileOperationLock _fileLock;
    private readonly IFileWatcherService _watcher;
    private readonly IFileCoverCacheService _coverCache;
    private readonly IFileStoreService _fileStore;
    private readonly INotificationPublisher _notifications;
    private readonly ILogger<ComicPageRemover> _logger;

    public ComicPageRemover(
        IFileOperationLock fileLock,
        IFileWatcherService watcher,
        IFileCoverCacheService coverCache,
        IFileStoreService fileStore,
        INotificationPublisher notifications,
        ILogger<ComicPageRemover> logger)
    {
        _fileLock = fileLock;
        _watcher = watcher;
        _coverCache = coverCache;
        _fileStore = fileStore;
        _notifications = notifications;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PageRemovalResult> RemovePagesAsync(string filePath, IReadOnlyCollection<string> pageNames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pageNames);
        var names = pageNames.Where(name => !string.IsNullOrEmpty(name)).ToHashSet(StringComparer.Ordinal);
        if (names.Count == 0)
        {
            return PageRemovalResult.Refused("Choose the pages to remove");
        }

        if (string.IsNullOrWhiteSpace(filePath) || await _fileStore.GetFileAsync(filePath, cancellationToken) is null)
        {
            return PageRemovalResult.Refused("The file is not in the library");
        }

        if (!ComicFileExtensions.IsWritableArchive(filePath))
        {
            return PageRemovalResult.Refused("Only a CBZ's pages can be removed; convert this file to CBZ first");
        }

        PageRemovalResult result;
        using (await _fileLock.AcquireAsync(filePath, cancellationToken))
        {
            // The archive may have been renamed or deleted while the lock was awaited.
            if (!File.Exists(filePath))
            {
                return PageRemovalResult.Refused("The file is gone");
            }

            try
            {
                result = RewriteWithout(filePath, names, () => _watcher.SuppressProcessing(filePath));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or SharpCompressException)
            {
                _logger.LogWarning(ex, "Could not remove pages from {Path}", LoggingHelper.SanitizePathForLog(filePath));
                return PageRemovalResult.Refused($"The archive could not be rewritten: {ex.Message}");
            }
        }

        if (result.Succeeded)
        {
            _logger.LogInformation(
                "Removed {Removed} page(s) from {Path}; {Left} left",
                result.Removed,
                LoggingHelper.SanitizePathForLog(filePath),
                result.PagesLeft);
            await _coverCache.InvalidateAsync(filePath, cancellationToken);
            await _fileStore.RefreshFileStatsAsync([filePath], cancellationToken);
            _notifications.LibraryChanged();
        }

        return result;
    }

    /// <summary>
    /// Writes <paramref name="archivePath"/> again without the pages named <paramref name="names"/>,
    /// with ComicInfo.xml's page list following (see <see cref="WithoutPages"/>).
    /// </summary>
    internal static PageRemovalResult RewriteWithout(string archivePath, IReadOnlySet<string> names, Action? onBeforeReplace = null)
    {
        using var source = ComicArchiveReader.Open(archivePath);
        var pages = source.ListPages();
        var unknown = names.Where(name => !pages.Any(page => string.Equals(page.Name, name, StringComparison.Ordinal))).ToList();
        if (unknown.Count > 0)
        {
            return PageRemovalResult.Refused(unknown.Count == names.Count
                ? "Those pages are not in the archive any more; reload its pages"
                : $"{unknown.Count.ToString(CultureInfo.InvariantCulture)} of those pages are not in the archive any more; reload its pages");
        }

        // Every entry of a removed name goes, so count by position.
        var removedPositions = pages
            .Select((page, index) => (page, index))
            .Where(item => names.Contains(item.page.Name))
            .Select(item => item.index)
            .ToList();
        var left = pages.Count - removedPositions.Count;
        if (left == 0)
        {
            return PageRemovalResult.Refused("A comic must keep at least one page");
        }

        var comicInfo = source.FindComicInfoEntry();
        byte[]? comicInfoBytes = null;
        if (comicInfo is not null && WithoutPages(comicInfo.ReadText(), removedPositions, left) is { } updated)
        {
            comicInfoBytes = ComicInfoXml.ToUtf8Bytes(updated);
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
                    source.CopyFileEntriesTo(writer, name => !names.Contains(name)
                        && !(comicInfoBytes is not null && string.Equals(name, comicInfo!.Name, StringComparison.Ordinal)));
                    if (comicInfoBytes is not null)
                    {
                        using var stream = new MemoryStream(comicInfoBytes, writable: false);
                        writer.Write(comicInfo!.Name.Replace('\\', '/'), stream, DateTime.Now);
                    }
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
            return new PageRemovalResult(true, removedPositions.Count, left, null);
        }
        catch
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// ComicInfo.xml <paramref name="xml"/> without the pages at <paramref name="removed"/>
    /// (0-based positions in page order): their <c>&lt;Page Image="n"&gt;</c> entries go, later
    /// ones are numbered down to their new positions, and <c>&lt;PageCount&gt;</c> becomes
    /// <paramref name="pagesLeft"/>. Null when it has neither, or is not XML: it is copied as it is.
    /// </summary>
    internal static XDocument? WithoutPages(string xml, IReadOnlyCollection<int> removed, int pagesLeft)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return null;
        }

        if (document.Root is not { } root)
        {
            return null;
        }

        var ns = root.Name.Namespace;
        var changed = false;
        if (root.Element(ns + "PageCount") is { } pageCount)
        {
            pageCount.Value = pagesLeft.ToString(CultureInfo.InvariantCulture);
            changed = true;
        }

        if (root.Element(ns + "Pages") is { } list)
        {
            var gone = removed.ToHashSet();
            foreach (var page in list.Elements(ns + "Page").ToList())
            {
                if (!int.TryParse((string?)page.Attribute("Image"), NumberStyles.None, CultureInfo.InvariantCulture, out var image))
                {
                    continue;
                }

                if (gone.Contains(image))
                {
                    page.Remove();
                }
                else
                {
                    page.SetAttributeValue("Image", (image - gone.Count(position => position < image)).ToString(CultureInfo.InvariantCulture));
                }

                changed = true;
            }
        }

        return changed ? document : null;
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
