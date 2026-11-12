using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Notifications;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpCompress.Common;
using SharpCompress.Writers.Zip;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default <see cref="ISeriesArchiveCoverWriter"/> implementation. Embeds the
/// canonical cached series cover into the first comic archive of the series
/// as a root-level <c>cover.&lt;ext&gt;</c> entry.
///
/// <para>Design notes:</para>
/// <list type="bullet">
///   <item>Only writable CBZ archives are modified. The on-disk entry name is
///   pinned to <c>cover.&lt;ext&gt;</c> at the archive root and never derives
///   from the provider URL or normalized key.</item>
///   <item>Writes are idempotent: when the archive already contains a single
///   byte-identical <c>cover.&lt;ext&gt;</c> entry the rewrite is skipped.
///   This keeps repeated cover-set calls (and any reprocessing the rewrite
///   itself might trigger) from churning the archive.</item>
///   <item>All operations are best-effort: failures are logged and swallowed.</item>
/// </list>
/// </summary>
public class SeriesArchiveCoverWriter : ISeriesArchiveCoverWriter
{
    // The complete set of extensions we ever write as a cover entry. Used by
    // RemoveAsync (and the replace step of WriteAsync) to delete any cover.*
    // a previous write may have placed, including a different extension than
    // the current cover.
    private static readonly string[] ManagedExtensions = { ".jpg", ".png", ".webp" };

    // Map allowed image content-types to the on-disk extension. Mirrors the
    // allow-list in SeriesImageStore / SeriesFolderCoverWriter.
    private static readonly IReadOnlyDictionary<string, string> ContentTypeToExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpeg"] = ".jpg",
            ["image/png"] = ".png",
            ["image/webp"] = ".webp"
        };

    private readonly ISeriesLibraryService _library;
    private readonly IFileWatcherService _watcher;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<SeriesArchiveCoverWriter> _logger;
    private readonly IFileOperationLock _fileLock;
    private readonly INotificationPublisher _notifications;

    public SeriesArchiveCoverWriter(
        ISeriesLibraryService library,
        IFileWatcherService watcher,
        IOptionsMonitor<AppSettings> settings,
        ILogger<SeriesArchiveCoverWriter> logger,
        IFileOperationLock fileLock,
        INotificationPublisher notifications)
    {
        _library = library;
        _watcher = watcher;
        _settings = settings;
        _logger = logger;
        // Shared with the file processor through DI, so embedding a cover never rewrites an
        // archive that is being normalized, renamed or edited at the same time.
        _fileLock = fileLock;
        _notifications = notifications;
    }

    public async Task WriteAsync(
        string normalizedKey,
        string sourceFilePath,
        string contentType,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (!force && !_settings.CurrentValue.WriteCoverToFirstArchive) return;
        if (string.IsNullOrWhiteSpace(normalizedKey)) return;
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            return;
        }

        if (!ContentTypeToExtension.TryGetValue((contentType ?? string.Empty).Trim().ToLowerInvariant(), out var extension))
        {
            _logger.LogDebug(
                "Skipping first-archive cover write for {Key}: unsupported content-type {ContentType}",
                LoggingHelper.SanitizeForLog(normalizedKey),
                LoggingHelper.SanitizeForLog(contentType ?? string.Empty));
            return;
        }

        var archivePath = await ResolveFirstArchiveAsync(normalizedKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return;
        }

        // Only writable CBZ (ZIP) archives can be rewritten; CBR is read-only.
        if (!ComicFileExtensions.IsWritableArchive(archivePath))
        {
            _logger.LogDebug(
                "Skipping first-archive cover write for {Key}: first archive {Path} is not a writable CBZ",
                LoggingHelper.SanitizeForLog(normalizedKey),
                LoggingHelper.SanitizePathForLog(archivePath));
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        byte[] coverBytes;
        try
        {
            coverBytes = await File.ReadAllBytesAsync(sourceFilePath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex,
                "Failed to read source cover image for first-archive embed for {Key}",
                LoggingHelper.SanitizeForLog(normalizedKey));
            return;
        }

        var coverEntryName = "cover" + extension;

        try
        {
            using (await _fileLock.AcquireAsync(archivePath, cancellationToken))
            {
                // The archive may have been renamed or deleted while the lock was awaited.
                if (!File.Exists(archivePath))
                {
                    return;
                }

                RewriteArchive(
                    archivePath,
                    coverEntryName,
                    coverBytes,
                    onBeforeReplace: () => SuppressWatcherProcessing(archivePath),
                    onAfterReplace: () => OnArchiveReplaced(archivePath));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex,
                "Failed to embed series cover into first archive {Path} for {Key}",
                LoggingHelper.SanitizePathForLog(archivePath),
                LoggingHelper.SanitizeForLog(normalizedKey));
        }
    }

    public async Task RemoveAsync(
        string normalizedKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedKey)) return;

        var archivePath = await ResolveFirstArchiveAsync(normalizedKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return;
        }

        if (!ComicFileExtensions.IsWritableArchive(archivePath))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using (await _fileLock.AcquireAsync(archivePath, cancellationToken))
            {
                if (!File.Exists(archivePath))
                {
                    return;
                }

                // Passing no cover bytes removes any managed cover.* entry without
                // adding a replacement.
                RewriteArchive(
                    archivePath,
                    coverEntryName: null,
                    coverBytes: null,
                    onBeforeReplace: () => SuppressWatcherProcessing(archivePath),
                    onAfterReplace: () => OnArchiveReplaced(archivePath));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArchiveLimitExceededException ex)
        {
            _logger.LogWarning(
                "Left the embedded series cover in {Path} for {Key}, the archive was refused: {Reason}",
                LoggingHelper.SanitizePathForLog(archivePath),
                LoggingHelper.SanitizeForLog(normalizedKey),
                LoggingHelper.SanitizeForLog(ex.Message));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex,
                "Failed to remove embedded series cover from first archive {Path} for {Key}",
                LoggingHelper.SanitizePathForLog(archivePath),
                LoggingHelper.SanitizeForLog(normalizedKey));
        }
    }

    /// <summary>Runs only when the archive really was rewritten, not when it already matched.</summary>
    private void OnArchiveReplaced(string archivePath)
    {
        // Re-flag after the move: the overwrite fires its own file-system events, which may
        // arrive slightly after the pre-move mark.
        SuppressWatcherProcessing(archivePath);
        _notifications.LibraryChanged();
    }

    private async Task<string?> ResolveFirstArchiveAsync(
        string normalizedKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _library.GetFirstIssueFilePathForNormalizedKeyAsync(normalizedKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex,
                "Failed to resolve first archive for series cover embed/remove for {Key}",
                LoggingHelper.SanitizeForLog(normalizedKey));
            return null;
        }
    }

    /// <summary>
    /// Marks the archive path as a self-induced change on the file watcher so
    /// the cover rewrite does not get picked up and reprocessed.
    /// </summary>
    private void SuppressWatcherProcessing(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return;
        }

        try
        {
            _watcher.SuppressProcessing(archivePath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex,
                "Failed to suppress watcher processing for first-archive cover write {Path}",
                LoggingHelper.SanitizePathForLog(archivePath));
        }
    }

    /// <summary>
    /// Rewrite <paramref name="archivePath"/> so that it contains exactly the
    /// desired managed cover state. When <paramref name="coverBytes"/> is
    /// non-null the archive ends up with a single <paramref name="coverEntryName"/>
    /// entry carrying those bytes (any other managed <c>cover.*</c> entries are
    /// dropped); when null, all managed <c>cover.*</c> entries are removed.
    ///
    /// The rewrite is skipped entirely when the archive already matches the
    /// desired state, making repeated calls idempotent. Non-cover entries are
    /// always preserved and are streamed one at a time, so memory use does not
    /// grow with entry or archive size. The new archive is written to a sibling
    /// temp file (named by <see cref="ArchiveWriteRecoveryService.CreateTempPath"/>,
    /// so the watcher and library scan ignore it and the startup sweep removes it
    /// if a crash leaves it behind), flushed to disk and atomically renamed over
    /// the original, which is never truncated or rewritten in place.
    /// </summary>
    internal static void RewriteArchive(
        string archivePath,
        string? coverEntryName,
        byte[]? coverBytes,
        Action? onBeforeReplace = null,
        Action? onAfterReplace = null)
    {
        using var source = ComicArchiveReader.Open(archivePath);

        var existingCoverEntries = source.EnumerateEntries()
            .Where(e => !e.IsDirectory && IsManagedCoverEntry(e.Name))
            .ToList();

        // Idempotency / no-op checks: avoid rewriting (and the file-system
        // churn / reprocessing that a rewrite implies) when the archive is
        // already in the desired state.
        if (coverBytes is null)
        {
            if (existingCoverEntries.Count == 0)
            {
                return;
            }
        }
        else if (existingCoverEntries.Count == 1
            && string.Equals(existingCoverEntries[0].Name, coverEntryName, StringComparison.OrdinalIgnoreCase)
            && EntryBytesEqual(existingCoverEntries[0], coverBytes))
        {
            return;
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
                    // Copy every non-managed-cover entry verbatim, under the rewrite limits.
                    source.CopyFileEntriesTo(writer, name => !IsManagedCoverEntry(name));

                    if (coverBytes is not null && !string.IsNullOrEmpty(coverEntryName))
                    {
                        using var coverStream = new MemoryStream(coverBytes, writable: false);
                        writer.Write(coverEntryName, coverStream, DateTime.Now);
                    }
                }

                // The zip is complete once the writer has written its central directory. Make it
                // durable before it is renamed over the original, so a power loss cannot leave an
                // empty or partial file in its place.
                destination.Flush(flushToDisk: true);
            }

            CopyUnixFileMode(archivePath, tempFile);

            // Release the source handle before replacing the file on disk.
            source.Dispose();

            // Flag the imminent on-disk change as self-induced so the file
            // watcher does not reprocess the archive we are about to rewrite.
            onBeforeReplace?.Invoke();

            File.Move(tempFile, archivePath, overwrite: true);

            // Re-flag after the move: the overwrite fires its own file-system
            // events, which may arrive slightly after the pre-move mark.
            onAfterReplace?.Invoke();
        }
        catch
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
            throw;
        }
    }

    /// <summary>
    /// Gives the rewritten archive the original's permission bits, so replacing it does not
    /// change who may read the file. Best effort; ownership stays with the application's user.
    /// </summary>
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

    private static bool IsManagedCoverEntry(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        // Root-level entries only: a "cover.jpg" nested in a sub-folder is not
        // something this writer placed and must not be touched.
        if (key.IndexOf('/') >= 0 || key.IndexOf('\\') >= 0)
        {
            return false;
        }

        foreach (var ext in ManagedExtensions)
        {
            if (string.Equals(key, "cover" + ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool EntryBytesEqual(ComicArchiveEntry entry, byte[] expected)
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
}
