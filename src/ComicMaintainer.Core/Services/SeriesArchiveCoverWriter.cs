using System.IO.Compression;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default <see cref="ISeriesArchiveCoverWriter"/> implementation. Embeds the
/// canonical cached series cover into the first issue's CBZ as a single page
/// that sorts ahead of every real page, so readers display it as the series
/// cover.
///
/// <para>Design notes:</para>
/// <list type="bullet">
///   <item>The embedded page is named <c>0000-cmcover.&lt;ext&gt;</c>. The
///   leading zeros + <c>-</c> separator make it sort before conventional page
///   names (e.g. <c>001.jpg</c>, <c>page 01.jpg</c>) under the same natural
///   comparer the reader uses, and the <c>cmcover</c> sentinel lets us find
///   and replace/remove our own page without disturbing the real pages.</item>
///   <item><see cref="ISeriesLibraryService"/> is resolved lazily through
///   <see cref="IServiceScopeFactory"/> to avoid a construction-time cycle
///   (the metadata cache service that calls this writer is itself a
///   dependency of the library service), mirroring
///   <see cref="SeriesFolderCoverWriter"/>.</item>
///   <item>Only writable CBZ first issues are modified; CBR (and any other
///   non-writable archive) are skipped. All operations are best-effort:
///   failures are logged and swallowed.</item>
/// </list>
/// </summary>
public class SeriesArchiveCoverWriter : ISeriesArchiveCoverWriter
{
    // Base name (without extension) of the embedded cover page. Anything with
    // this stem is treated as a managed cover and removed before a fresh write
    // so the archive ends up with exactly one managed cover page.
    private const string CoverEntryStem = "0000-cmcover";

    private static readonly string[] ManagedExtensions = { ".jpg", ".jpeg", ".png", ".webp" };

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

    public async Task<bool> WriteAsync(
        string normalizedKey,
        string sourceFilePath,
        string contentType,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (!force && !_settings.CurrentValue.WriteCoverToFirstArchive) return false;
        if (string.IsNullOrWhiteSpace(normalizedKey)) return false;
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            return false;
        }

        if (!ContentTypeToExtension.TryGetValue((contentType ?? string.Empty).Trim().ToLowerInvariant(), out var extension))
        {
            _logger.LogDebug(
                "Skipping first-archive cover embed for {Key}: unsupported content-type {ContentType}",
                LoggingHelper.SanitizeForLog(normalizedKey),
                LoggingHelper.SanitizeForLog(contentType ?? string.Empty));
            return false;
        }

        var firstIssue = await ResolveFirstIssueAsync(normalizedKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(firstIssue))
        {
            return false;
        }

        if (!File.Exists(firstIssue) || !ComicFileExtensions.IsWritableArchive(firstIssue))
        {
            _logger.LogDebug(
                "Skipping first-archive cover embed for {Key}: first issue {Path} is missing or not a writable CBZ",
                LoggingHelper.SanitizeForLog(normalizedKey),
                LoggingHelper.SanitizePathForLog(firstIssue));
            return false;
        }

        byte[] coverBytes;
        try
        {
            coverBytes = await File.ReadAllBytesAsync(sourceFilePath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Failed to read source cover {Path} for first-archive embed of {Key}",
                LoggingHelper.SanitizePathForLog(sourceFilePath),
                LoggingHelper.SanitizeForLog(normalizedKey));
            return false;
        }

        var coverEntryName = CoverEntryStem + extension;

        try
        {
            return await Task.Run(
                () => EmbedCover(firstIssue, coverEntryName, coverBytes),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to embed cover into first issue {Path} for {Key}",
                LoggingHelper.SanitizePathForLog(firstIssue),
                LoggingHelper.SanitizeForLog(normalizedKey));
            return false;
        }
    }

    public async Task RemoveAsync(
        string normalizedKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedKey)) return;

        var firstIssue = await ResolveFirstIssueAsync(normalizedKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(firstIssue)) return;
        if (!File.Exists(firstIssue) || !ComicFileExtensions.IsWritableArchive(firstIssue)) return;

        try
        {
            await Task.Run(
                () => EmbedCover(firstIssue, coverEntryName: null, coverBytes: null),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Failed to remove embedded cover from first issue {Path} for {Key}",
                LoggingHelper.SanitizePathForLog(firstIssue),
                LoggingHelper.SanitizeForLog(normalizedKey));
        }
    }

    /// <summary>
    /// Rewrites <paramref name="archivePath"/> so it contains exactly one
    /// managed cover page (when <paramref name="coverEntryName"/> /
    /// <paramref name="coverBytes"/> are supplied) or none (when both are
    /// null). Returns true when the archive was rewritten; false when it was
    /// already in the desired state (idempotent no-op). Runs synchronously;
    /// callers offload it to a worker thread.
    /// </summary>
    private static bool EmbedCover(string archivePath, string? coverEntryName, byte[]? coverBytes)
    {
        // Inspect the current managed-cover state before deciding whether a
        // rewrite is needed, so repeated writes of the same cover are cheap.
        using (var existing = ZipFile.OpenRead(archivePath))
        {
            var managed = existing.Entries
                .Where(e => IsManagedCoverEntry(e.FullName))
                .ToList();

            if (coverBytes is null)
            {
                // Remove path: nothing to do when no managed cover is present.
                if (managed.Count == 0)
                {
                    return false;
                }
            }
            else
            {
                // Write path: skip the rewrite when the only entry is our
                // managed cover, with the expected name and identical bytes.
                if (managed.Count == 1
                    && string.Equals(Path.GetFileName(managed[0].FullName), coverEntryName, StringComparison.OrdinalIgnoreCase)
                    && EntryMatches(managed[0], coverBytes))
                {
                    return false;
                }
            }
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(archivePath)) ?? Path.GetTempPath();
        var tempPath = Path.Combine(directory, $".{Guid.NewGuid():N}.cbz.tmp");

        try
        {
            using (var source = ZipFile.OpenRead(archivePath))
            using (var tempStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var dest = new ZipArchive(tempStream, ZipArchiveMode.Create))
            {
                // New managed cover first (sorts ahead regardless of write
                // order, but keeping it first is tidy).
                if (coverBytes is not null && coverEntryName is not null)
                {
                    var coverEntry = dest.CreateEntry(coverEntryName, CompressionLevel.Optimal);
                    using var coverStream = coverEntry.Open();
                    coverStream.Write(coverBytes, 0, coverBytes.Length);
                }

                foreach (var entry in source.Entries)
                {
                    // Skip directory placeholders and any prior managed cover
                    // (in any extension) so the result has exactly one cover.
                    if (entry.FullName.EndsWith('/'))
                    {
                        continue;
                    }
                    if (IsManagedCoverEntry(entry.FullName))
                    {
                        continue;
                    }

                    var copy = dest.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                    copy.LastWriteTime = entry.LastWriteTime;
                    using var sourceStream = entry.Open();
                    using var copyStream = copy.Open();
                    sourceStream.CopyTo(copyStream);
                }
            }

            // Atomic replace.
            File.Move(tempPath, archivePath, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { /* best-effort */ }
            }
        }
    }

    private static bool IsManagedCoverEntry(string entryName)
    {
        if (string.IsNullOrEmpty(entryName)) return false;
        var fileName = Path.GetFileName(entryName);
        if (string.IsNullOrEmpty(fileName)) return false;
        var ext = Path.GetExtension(fileName);
        if (!ManagedExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return string.Equals(stem, CoverEntryStem, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EntryMatches(ZipArchiveEntry entry, byte[] expected)
    {
        if (entry.Length != expected.LongLength)
        {
            return false;
        }
        using var stream = entry.Open();
        using var memory = new MemoryStream(expected.Length);
        stream.CopyTo(memory);
        var actual = memory.GetBuffer();
        for (var i = 0; i < expected.Length; i++)
        {
            if (actual[i] != expected[i])
            {
                return false;
            }
        }
        return true;
    }

    private async Task<string?> ResolveFirstIssueAsync(
        string normalizedKey,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var library = scope.ServiceProvider.GetRequiredService<ISeriesLibraryService>();
            return await library.GetFirstIssueFilePathForNormalizedKeyAsync(normalizedKey, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Failed to resolve first issue for first-archive cover write/remove for {Key}",
                LoggingHelper.SanitizeForLog(normalizedKey));
            return null;
        }
    }
}
