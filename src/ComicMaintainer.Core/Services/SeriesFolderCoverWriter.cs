using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default <see cref="ISeriesFolderCoverWriter"/> implementation. Copies the
/// canonical cached series cover into each on-disk folder that contains
/// files for the series, as <c>cover.&lt;ext&gt;</c>.
///
/// <para>Design notes:</para>
/// <list type="bullet">
///   <item><see cref="ISeriesLibraryService"/> is resolved lazily through
///   <see cref="IServiceScopeFactory"/> because the metadata cache service
///   (which calls this writer) is itself a dependency of the library
///   service. Resolving at call-time avoids a construction-time cycle.</item>
///   <item>All write paths run through <see cref="ResolveCoverPath"/> which
///   pins the file name to <c>cover.&lt;ext&gt;</c> and re-validates that
///   the destination stays inside the target folder. The provider URL and
///   normalized key never influence the on-disk name.</item>
///   <item>All operations are best-effort: per-folder failures are logged
///   and the loop continues. Total failure (no folders, feature disabled,
///   missing source file) is silent.</item>
/// </list>
/// </summary>
public class SeriesFolderCoverWriter : ISeriesFolderCoverWriter
{
    // The complete set of extensions we ever write. Used by RemoveAsync to
    // delete any cover.* that a previous write may have placed, including
    // ones in a different extension than the current cover.
    private static readonly string[] ManagedExtensions = { ".jpg", ".png", ".webp" };

    // Map allowed image content-types to the on-disk extension. Mirrors the
    // allow-list in SeriesImageStore so the two stay in sync.
    private static readonly IReadOnlyDictionary<string, string> ContentTypeToExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpeg"] = ".jpg",
            ["image/png"] = ".png",
            ["image/webp"] = ".webp"
        };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<SeriesFolderCoverWriter> _logger;

    public SeriesFolderCoverWriter(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AppSettings> settings,
        ILogger<SeriesFolderCoverWriter> logger)
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
        if (!force && !_settings.CurrentValue.WriteCoverToSeriesFolder) return;
        if (string.IsNullOrWhiteSpace(normalizedKey)) return;
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            return;
        }

        if (!ContentTypeToExtension.TryGetValue((contentType ?? string.Empty).Trim().ToLowerInvariant(), out var extension))
        {
            _logger.LogDebug(
                "Skipping series-folder cover write for {Key}: unsupported content-type {ContentType}",
                LoggingHelper.SanitizeForLog(normalizedKey),
                LoggingHelper.SanitizeForLog(contentType ?? string.Empty));
            return;
        }

        var folders = await ResolveFoldersAsync(normalizedKey, cancellationToken);
        if (folders.Count == 0)
        {
            return;
        }

        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                // Remove any cover.* with a different extension first so each
                // folder ends up with exactly one canonical cover file.
                RemoveExistingCovers(folder, except: extension);

                var destination = ResolveCoverPath(folder, extension);
                if (destination is null)
                {
                    continue;
                }

                // Atomic write: copy to a sibling temp file, then move into
                // place. This avoids leaving a half-written cover.jpg if the
                // copy is interrupted (e.g. the destination disk fills up
                // mid-write).
                var tempPath = destination + ".tmp";
                try
                {
                    File.Copy(sourceFilePath, tempPath, overwrite: true);
                    File.Move(tempPath, destination, overwrite: true);
                }
                catch
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                    catch { /* best-effort */ }
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to write series cover to folder {Folder} for {Key}",
                    LoggingHelper.SanitizeForLog(folder),
                    LoggingHelper.SanitizeForLog(normalizedKey));
            }
        }
    }

    public async Task RemoveAsync(
        string normalizedKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedKey)) return;

        var folders = await ResolveFoldersAsync(normalizedKey, cancellationToken);
        if (folders.Count == 0)
        {
            return;
        }

        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }
                RemoveExistingCovers(folder, except: null);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Failed to remove series cover from folder {Folder} for {Key}",
                    LoggingHelper.SanitizeForLog(folder),
                    LoggingHelper.SanitizeForLog(normalizedKey));
            }
        }
    }

    private async Task<IReadOnlyList<string>> ResolveFoldersAsync(
        string normalizedKey,
        CancellationToken cancellationToken)
    {
        try
        {
            // Resolve the library service lazily through a fresh scope so
            // singleton-vs-singleton construction order remains acyclic.
            using var scope = _scopeFactory.CreateScope();
            var library = scope.ServiceProvider.GetRequiredService<ISeriesLibraryService>();
            var folders = await library.GetFoldersForNormalizedKeyAsync(normalizedKey, cancellationToken);
            return folders.Select(f => f.Directory)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Failed to resolve folders for series cover write/remove for {Key}",
                LoggingHelper.SanitizeForLog(normalizedKey));
            return Array.Empty<string>();
        }
    }

    private static void RemoveExistingCovers(string folder, string? except)
    {
        foreach (var ext in ManagedExtensions)
        {
            if (except is not null && string.Equals(ext, except, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var path = ResolveCoverPath(folder, ext);
            if (path is null) continue;
            if (!File.Exists(path)) continue;
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best-effort: caller logs the outer failure when relevant.
            }
        }
    }

    /// <summary>
    /// Build the absolute path for <c>cover.&lt;ext&gt;</c> in the target
    /// folder, refusing anything that would escape that folder. The folder
    /// path itself comes from the series library, which sources it from the
    /// indexed file store, so it is already trusted; this guard exists as
    /// defense in depth in case a malformed folder string ever slips in.
    /// </summary>
    private static string? ResolveCoverPath(string folder, string extension)
    {
        try
        {
            var fullFolder = Path.GetFullPath(folder);
            var fileName = "cover" + extension;
            var candidate = Path.GetFullPath(Path.Combine(fullFolder, fileName));
            var withSep = fullFolder.EndsWith(Path.DirectorySeparatorChar)
                ? fullFolder
                : fullFolder + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(withSep, StringComparison.Ordinal))
            {
                return null;
            }
            return candidate;
        }
        catch
        {
            return null;
        }
    }
}
