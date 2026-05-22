using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Scheduled-job handler that replaces the live <c>FileSystemWatcher</c> as
/// the primary mechanism for keeping per-file metadata up to date. A single
/// run performs three passes:
///
/// <list type="number">
///   <item><b>Reconcile additions</b> — walks the watched directory and
///     enqueues full <c>ProcessFileAsync</c> for any comic file present on
///     disk but missing from the file store (and adds it to the store).
///     </item>
///   <item><b>Reconcile deletions</b> — when enabled, removes tracked rows
///     whose files no longer exist on disk (replacing the live
///     <c>OnFileDeleted</c> handler).</item>
///   <item><b>Stale-metadata retag</b> — when enabled, compares each
///     tracked file's <c>SeriesMetadataVersion</c> stamp to the matching
///     cache record's current <c>MetadataVersion</c> and forces a
///     <c>NormalizeFilesAsync</c> pass over any file whose stamp is lower.
///     This is the key fix for "stuck" series: a canonical-title edit,
///     alias change, language-preference change, or fresh external match
///     all bump the cache record's version on write, so the very next
///     scheduled scan retags every affected file without anyone clicking
///     a button.</item>
/// </list>
///
/// The handler is disabled by default in this step so existing
/// installations can opt in deliberately. A subsequent change will flip
/// the default to enabled and deprecate the live watcher's normalize/rename
/// triggers.
/// </summary>
public class LibraryScanJobHandler : IScheduledJobHandler
{
    public const string Key = "library-scan";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly ISeriesMetadataCacheService? _seriesCache;
    private readonly ILogger<LibraryScanJobHandler> _logger;

    public LibraryScanJobHandler(
        IOptionsMonitor<AppSettings> settings,
        IFileStoreService fileStore,
        IComicProcessorService processor,
        ILogger<LibraryScanJobHandler> logger,
        ISeriesMetadataCacheService? seriesCache = null)
    {
        _settings = settings;
        _fileStore = fileStore;
        _processor = processor;
        _logger = logger;
        _seriesCache = seriesCache;
    }

    public string JobKey => Key;
    public string DisplayName => "Library Scan";
    public string Description =>
        "Walks the watched directory, adds new files, reconciles deletions, and re-normalizes files whose series-metadata stamp is older than the current cache record (stale-metadata retag). Designed to replace the live file-system watcher as the primary per-file metadata refresher.";

    // Default: disabled with an hourly cadence. Operators opt in deliberately
    // until step 4 of the plan, which flips the default and deprecates the
    // live watcher.
    public ScheduledJobDefaults Defaults =>
        new(Enabled: false, IntervalMinutes: 60,
            OptionsJson: """{"scanForNew":true,"reconcileDeletions":true,"retagStale":true}""");

    public async Task<string> ExecuteAsync(string? optionsJson, CancellationToken cancellationToken)
    {
        var options = ParseOptions(optionsJson);

        var watchedDir = _settings.CurrentValue.WatchedDirectory;
        if (string.IsNullOrWhiteSpace(watchedDir) || !Directory.Exists(watchedDir))
        {
            return $"Watched directory not available: '{watchedDir}'";
        }

        var addedCount = 0;
        var removedCount = 0;
        var staleCount = 0;

        // Pass 1: enumerate the filesystem and add unknown files.
        // Mirrors FileWatcherService.ScanExistingFilesAsync but also queues
        // a full ProcessFileAsync for additions (the watcher's scan path
        // only adds rows; live FS events were what triggered processing).
        HashSet<string>? diskPaths = null;
        if (options.ScanForNew || options.ReconcileDeletions)
        {
            diskPaths = new HashSet<string>(
                Directory.EnumerateFiles(watchedDir, "*.*", SearchOption.AllDirectories)
                    .Where(ComicFileExtensions.IsComicArchive),
                StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation("Library scan: found {Count} comic file(s) on disk under {Dir}",
                diskPaths.Count, LoggingHelper.SanitizePathForLog(watchedDir));
        }

        var newPaths = new List<string>();
        if (options.ScanForNew && diskPaths is not null)
        {
            foreach (var path in diskPaths)
            {
                if (cancellationToken.IsCancellationRequested) break;
                try
                {
                    if (!await _fileStore.FileExistsAsync(path, cancellationToken))
                    {
                        await _fileStore.AddFileAsync(path, cancellationToken);
                        newPaths.Add(path);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Library scan: failed to add new file {Path}",
                        LoggingHelper.SanitizePathForLog(path));
                }
            }
            addedCount = newPaths.Count;
        }

        // Pass 2: reconcile deletions. Drop rows whose files no longer
        // exist on disk so the in-memory store and DB don't accumulate
        // ghosts when the live watcher is off.
        if (options.ReconcileDeletions && diskPaths is not null)
        {
            try
            {
                var tracked = (await _fileStore.GetAllFilesAsync(cancellationToken))
                    .Select(f => f.FilePath)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();

                foreach (var path in tracked)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    if (!diskPaths.Contains(path))
                    {
                        try
                        {
                            await _fileStore.RemoveFileAsync(path, cancellationToken);
                            removedCount++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Library scan: failed to remove deleted file {Path}",
                                LoggingHelper.SanitizePathForLog(path));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Library scan: reconcile-deletions pass failed");
            }
        }

        // Pass 3: stale-metadata retag. Group tracked files by their
        // folder-derived series key (the same key the resolver uses), look
        // up the current cache record's MetadataVersion for each group,
        // and ask the file store which files in that group have a stamp
        // older than the current version. Force-normalize the result.
        var staleToFix = new List<string>();
        if (options.RetagStale && _seriesCache is not null)
        {
            try
            {
                var allFiles = (await _fileStore.GetAllFilesAsync(cancellationToken)).ToList();
                var byKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in allFiles)
                {
                    if (string.IsNullOrWhiteSpace(file.FilePath)) continue;
                    var folder = Path.GetFileName(Path.GetDirectoryName(file.FilePath));
                    if (string.IsNullOrWhiteSpace(folder)) continue;
                    var key = _seriesCache.NormalizeKey(folder);
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    if (!byKey.TryGetValue(key, out var list))
                    {
                        list = new List<string>();
                        byKey[key] = list;
                    }
                    list.Add(file.FilePath);
                }

                foreach (var (key, paths) in byKey)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    var record = await _seriesCache.GetAsync(key, cancellationToken);
                    if (record is null) continue;
                    var stale = await _fileStore.GetFilesWithStaleSeriesMetadataAsync(
                        paths, record.MetadataVersion, cancellationToken);
                    if (stale.Count > 0)
                    {
                        staleToFix.AddRange(stale);
                    }
                }

                // De-duplicate (newly-added files already enqueued above
                // would otherwise be normalized twice).
                staleToFix = staleToFix
                    .Where(p => !newPaths.Contains(p, StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                staleCount = staleToFix.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Library scan: stale-metadata pass failed");
            }
        }

        // Submit normalize jobs and wait for completion so the scheduled-job
        // last-run summary reflects the actual outcome (matching the
        // pattern used by FileNamingAuditJobHandler / MetadataAuditJobHandler).
        // For newly-added files we go through the full normalize+rename
        // pipeline (matching what the live watcher did). For stale files we
        // only need to re-normalize (the filename is already correct); we
        // use NormalizeFilesAsync with forceReprocess=true so the
        // IsProcessed gate doesn't short-circuit them.
        var newProcessedSummary = await ProcessNewFilesAndWaitAsync(newPaths, cancellationToken);
        var staleProcessedSummary = await NormalizeStaleFilesAndWaitAsync(staleToFix, cancellationToken);

        return $"Library scan: +{addedCount} added, -{removedCount} removed, {staleCount} stale retagged.{newProcessedSummary}{staleProcessedSummary}";
    }

    private async Task<string> ProcessNewFilesAndWaitAsync(IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        if (files.Count == 0) return string.Empty;
        Guid jobId;
        try
        {
            jobId = await _processor.NormalizeAndRenameFilesAsync(files, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Library scan: failed to queue {Count} added file(s)", files.Count);
            return " [added: queue failed]";
        }
        return await WaitForJobAsync(jobId, files.Count, "added", cancellationToken);
    }

    private async Task<string> NormalizeStaleFilesAndWaitAsync(IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        if (files.Count == 0) return string.Empty;
        Guid jobId;
        try
        {
            jobId = await _processor.NormalizeFilesAsync(files, forceReprocess: true, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Library scan: failed to queue {Count} stale file(s)", files.Count);
            return " [stale: queue failed]";
        }
        return await WaitForJobAsync(jobId, files.Count, "stale", cancellationToken);
    }

    private async Task<string> WaitForJobAsync(Guid jobId, int requested, string label, CancellationToken cancellationToken)
    {
        ProcessingJob? job = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            job = _processor.GetJob(jobId);
            if (job is null) break;
            if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled) break;
            await Task.Delay(PollInterval, cancellationToken);
        }
        return job is null
            ? $" [{label}: queued (job record evicted)]"
            : $" [{label}: {job.ProcessedFiles}/{requested} ok, {job.FailedFiles} failed]";
    }

    private static LibraryScanOptions ParseOptions(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson)) return new LibraryScanOptions();
        try
        {
            return JsonSerializer.Deserialize<LibraryScanOptions>(
                       optionsJson,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new LibraryScanOptions();
        }
        catch
        {
            return new LibraryScanOptions();
        }
    }

    private sealed class LibraryScanOptions
    {
        public bool ScanForNew { get; set; } = true;
        public bool ReconcileDeletions { get; set; } = true;
        public bool RetagStale { get; set; } = true;
    }
}
