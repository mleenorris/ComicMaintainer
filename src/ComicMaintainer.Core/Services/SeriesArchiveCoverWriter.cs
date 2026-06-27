using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default <see cref="ISeriesArchiveCoverWriter"/> implementation. Embeds the
/// canonical cached series cover into the first comic archive of the series
/// as a root-level <c>cover.&lt;ext&gt;</c> entry.
///
/// <para>Design notes:</para>
/// <list type="bullet">
///   <item><see cref="ISeriesLibraryService"/> is resolved lazily through
///   <see cref="IServiceScopeFactory"/> because the metadata cache service
///   (which calls this writer) is itself a dependency of the library
///   service. Resolving at call-time avoids a construction-time cycle. This
///   mirrors <see cref="SeriesFolderCoverWriter"/>.</item>
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

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<SeriesArchiveCoverWriter> _logger;

    public SeriesArchiveCoverWriter(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AppSettings> settings,
        ILogger<SeriesArchiveCoverWriter> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
        _logger = logger;
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read source cover image for first-archive embed for {Key}",
                LoggingHelper.SanitizeForLog(normalizedKey));
            return;
        }

        var coverEntryName = "cover" + extension;

        try
        {
            RewriteArchive(
                archivePath,
                coverEntryName,
                coverBytes,
                onBeforeReplace: () => SuppressWatcherProcessing(archivePath),
                onAfterReplace: () => SuppressWatcherProcessing(archivePath));
        }
        catch (Exception ex)
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
            // Passing no cover bytes removes any managed cover.* entry without
            // adding a replacement.
            RewriteArchive(
                archivePath,
                coverEntryName: null,
                coverBytes: null,
                onBeforeReplace: () => SuppressWatcherProcessing(archivePath),
                onAfterReplace: () => SuppressWatcherProcessing(archivePath));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Failed to remove embedded series cover from first archive {Path} for {Key}",
                LoggingHelper.SanitizePathForLog(archivePath),
                LoggingHelper.SanitizeForLog(normalizedKey));
        }
    }

    private async Task<string?> ResolveFirstArchiveAsync(
        string normalizedKey,
        CancellationToken cancellationToken)
    {
        try
        {
            // Resolve the library service lazily through a fresh scope so
            // singleton-vs-singleton construction order remains acyclic.
            using var scope = _scopeFactory.CreateScope();
            var library = scope.ServiceProvider.GetRequiredService<ISeriesLibraryService>();
            return await library.GetFirstIssueFilePathForNormalizedKeyAsync(normalizedKey, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Failed to resolve first archive for series cover embed/remove for {Key}",
                LoggingHelper.SanitizeForLog(normalizedKey));
            return null;
        }
    }

    /// <summary>
    /// Marks the archive path as a self-induced change on the file watcher so
    /// the cover rewrite does not get picked up and reprocessed. Resolved
    /// lazily through a fresh scope to keep singleton construction acyclic.
    /// </summary>
    private void SuppressWatcherProcessing(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var watcher = scope.ServiceProvider.GetService<IFileWatcherService>();
            watcher?.SuppressProcessing(archivePath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
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
    /// always preserved. The new archive is written to a sibling temp file and
    /// atomically moved into place.
    /// </summary>
    private static void RewriteArchive(
        string archivePath,
        string? coverEntryName,
        byte[]? coverBytes,
        Action? onBeforeReplace = null,
        Action? onAfterReplace = null)
    {
        using var source = ZipArchive.Open(archivePath);

        var existingCoverEntries = source.Entries
            .Where(e => !e.IsDirectory && IsManagedCoverEntry(e.Key))
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
            && string.Equals(existingCoverEntries[0].Key, coverEntryName, StringComparison.OrdinalIgnoreCase)
            && EntryBytesEqual(existingCoverEntries[0], coverBytes))
        {
            return;
        }

        var tempFile = archivePath + ".cover.tmp";
        try
        {
            using (var newArchive = ZipArchive.Create())
            {
                var entryStreams = new List<MemoryStream>();

                // Copy every non-managed-cover entry verbatim.
                foreach (var entry in source.Entries.Where(e => !e.IsDirectory))
                {
                    if (IsManagedCoverEntry(entry.Key))
                    {
                        continue;
                    }

                    using var entryStream = entry.OpenEntryStream();
                    var memStream = new MemoryStream();
                    entryStream.CopyTo(memStream);
                    memStream.Position = 0;
                    entryStreams.Add(memStream);

                    newArchive.AddEntry(entry.Key ?? "unknown", memStream, false, memStream.Length, entry.LastModifiedTime ?? DateTime.Now);
                }

                MemoryStream? coverStream = null;
                if (coverBytes is not null && !string.IsNullOrEmpty(coverEntryName))
                {
                    coverStream = new MemoryStream(coverBytes);
                    newArchive.AddEntry(coverEntryName, coverStream, false, coverBytes.Length, DateTime.Now);
                }

                using (var fileStream = File.Create(tempFile))
                {
                    newArchive.SaveTo(fileStream, new WriterOptions(CompressionType.Deflate)
                    {
                        LeaveStreamOpen = false
                    });
                }

                coverStream?.Dispose();
                foreach (var stream in entryStreams)
                {
                    stream.Dispose();
                }
            }

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
            catch { /* best-effort */ }
            throw;
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

    private static bool EntryBytesEqual(IArchiveEntry entry, byte[] expected)
    {
        try
        {
            if (entry.Size != expected.Length)
            {
                return false;
            }

            using var stream = entry.OpenEntryStream();
            using var memStream = new MemoryStream();
            stream.CopyTo(memStream);
            return memStream.ToArray().AsSpan().SequenceEqual(expected);
        }
        catch
        {
            return false;
        }
    }
}
