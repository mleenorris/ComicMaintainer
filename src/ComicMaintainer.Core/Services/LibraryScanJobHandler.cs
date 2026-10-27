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
///   <item><b>Modified-file reprocess</b> — opt-in via
///     <c>reprocessModified</c>: tracked files whose on-disk size or
///     last-write time differs from the stored stats (beyond a ~2s
///     tolerance) are force-normalized. Stored stats are refreshed only
///     for files the job normalized successfully, so our own metadata
///     writes don't retrigger them while a failed re-normalize is retried
///     by the next scan.</item>
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
        "Walks the watched directory, adds new files, reconciles deletions, and re-normalizes files whose series-metadata stamp is older than the current cache record (stale-metadata retag). Options: scanForNew, reconcileDeletions, retagStale, and reprocessModified (opt-in; re-normalizes tracked files whose on-disk size or modified time changed). Designed to replace the live file-system watcher as the primary per-file metadata refresher.";

    // Default: disabled with an hourly cadence. Operators opt in deliberately
    // until step 4 of the plan, which flips the default and deprecates the
    // live watcher.
    public ScheduledJobDefaults Defaults =>
        new(Enabled: false, IntervalMinutes: 60,
            OptionsJson: """{"scanForNew":true,"reconcileDeletions":true,"retagStale":true,"reprocessModified":false}""");

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
        var modifiedCount = 0;

        // Pass 1: enumerate the filesystem and add unknown files.
        // Mirrors FileWatcherService.ScanExistingFilesAsync but also queues
        // a full ProcessFileAsync for additions (the watcher's scan path
        // only adds rows; live FS events were what triggered processing).
        HashSet<string>? diskPaths = null;
        if (options.ScanForNew || options.ReconcileDeletions || options.ReprocessModified)
        {
            diskPaths = new HashSet<string>(
                Directory.EnumerateFiles(watchedDir, "*.*", SearchOption.AllDirectories)
                    .Where(ComicFileExtensions.IsComicArchive),
                StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation("Library scan: found {Count} comic file(s) on disk under {Dir}",
                diskPaths.Count, LoggingHelper.SanitizePathForLog(watchedDir));
        }

        // Load the tracked set once and reuse it for every pass instead of
        // issuing a FileExistsAsync / GetAllFilesAsync per pass or per file.
        var tracked = await LoadTrackedSnapshotAsync(cancellationToken);

        var newPaths = new List<string>();
        if (options.ScanForNew && diskPaths is not null)
        {
            foreach (var path in diskPaths)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (tracked.ContainsKey(path)) continue;
                try
                {
                    await _fileStore.AddFileAsync(path, cancellationToken);
                    newPaths.Add(path);
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
            foreach (var path in tracked.Keys.ToList())
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (diskPaths.Contains(path)) continue;
                try
                {
                    await _fileStore.RemoveFileAsync(path, cancellationToken);
                    tracked.Remove(path);
                    removedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Library scan: failed to remove deleted file {Path}",
                        LoggingHelper.SanitizePathForLog(path));
                }
            }
        }

        var newSet = new HashSet<string>(newPaths, StringComparer.OrdinalIgnoreCase);

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
                var byKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in tracked.Values)
                {
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
                    .Where(p => !newSet.Contains(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                staleCount = staleToFix.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Library scan: stale-metadata pass failed");
            }
        }

        // Pass 4 (opt-in): files modified on disk since they were last
        // tracked (size or last-write time differs from the stored stats),
        // e.g. replaced by an external tool. Force a re-normalize; the
        // stored stats are refreshed only once it succeeds (see below).
        var modifiedToFix = new List<string>();
        if (options.ReprocessModified && diskPaths is not null)
        {
            var staleSet = new HashSet<string>(staleToFix, StringComparer.OrdinalIgnoreCase);
            foreach (var file in tracked.Values)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (newSet.Contains(file.FilePath) || staleSet.Contains(file.FilePath)) continue;
                if (!diskPaths.Contains(file.FilePath)) continue;
                if (IsModifiedOnDisk(file)) modifiedToFix.Add(file.FilePath);
            }
            modifiedCount = modifiedToFix.Count;

            if (modifiedToFix.Count > 0)
            {
                _logger.LogInformation("Library scan: {Count} tracked file(s) changed on disk; re-normalizing",
                    modifiedToFix.Count);
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
        var (staleProcessedSummary, _) = await NormalizeFilesAndWaitAsync(staleToFix, "stale", cancellationToken);
        var (modifiedProcessedSummary, modifiedSucceeded) = await NormalizeFilesAndWaitAsync(modifiedToFix, "modified", cancellationToken);

        // Our own metadata writes change size/mtime; re-sync the stored
        // stats so the next scan doesn't see them as modified again. Only
        // files the job normalized successfully are re-synced: a file that
        // failed or couldn't be queued, or that a failed or cancelled job
        // didn't rewrite, keeps its old stats and is retried next scan.
        if (modifiedSucceeded.Count > 0)
        {
            await RefreshStatsSafeAsync(modifiedSucceeded, cancellationToken);
        }

        var modifiedPart = options.ReprocessModified ? $", {modifiedCount} modified reprocessed" : string.Empty;
        return $"Library scan: +{addedCount} added, -{removedCount} removed, {staleCount} stale retagged{modifiedPart}.{newProcessedSummary}{staleProcessedSummary}{modifiedProcessedSummary}";
    }

    /// <summary>
    /// Stored <see cref="ComicFile.LastModified"/> comes from
    /// <see cref="FileInfo.LastWriteTime"/> (local time); allow a small skew
    /// for filesystems with coarse timestamp resolution (FAT/SMB: 2s).
    /// </summary>
    private static readonly TimeSpan ModifiedTimeTolerance = TimeSpan.FromSeconds(2);

    private bool IsModifiedOnDisk(ComicFile file)
    {
        try
        {
            var info = new FileInfo(file.FilePath);
            if (!info.Exists) return false;
            if (info.Length != file.FileSize) return true;
            var delta = (info.LastWriteTime - file.LastModified).Duration();
            return delta > ModifiedTimeTolerance;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Library scan: stat failed for {Path}", LoggingHelper.SanitizePathForLog(file.FilePath));
            return false;
        }
    }

    private async Task RefreshStatsSafeAsync(IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        try
        {
            await _fileStore.RefreshFileStatsAsync(files, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Library scan: failed to refresh stored stats for {Count} file(s)", files.Count);
        }
    }

    private async Task<Dictionary<string, ComicFile>> LoadTrackedSnapshotAsync(CancellationToken cancellationToken)
    {
        var tracked = new Dictionary<string, ComicFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in await _fileStore.GetAllFilesAsync(cancellationToken) ?? Enumerable.Empty<ComicFile>())
        {
            if (file is null || string.IsNullOrWhiteSpace(file.FilePath)) continue;
            tracked.TryAdd(file.FilePath, file);
        }
        return tracked;
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
        return (await WaitForJobAsync(jobId, files.Count, "added", cancellationToken)).Summary;
    }

    private async Task<(string Summary, IReadOnlyList<string> Succeeded)> NormalizeFilesAndWaitAsync(
        IReadOnlyList<string> files, string label, CancellationToken cancellationToken)
    {
        if (files.Count == 0) return (string.Empty, Array.Empty<string>());
        Guid jobId;
        try
        {
            jobId = await _processor.NormalizeFilesAsync(files, forceReprocess: true, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Library scan: failed to queue {Count} {Label} file(s)", files.Count, label);
            return ($" [{label}: queue failed]", Array.Empty<string>());
        }
        var (summary, job) = await WaitForJobAsync(jobId, files.Count, label, cancellationToken);
        return (summary, SucceededFiles(files, job));
    }

    private async Task<(string Summary, ProcessingJob? Job)> WaitForJobAsync(Guid jobId, int requested, string label, CancellationToken cancellationToken)
    {
        ProcessingJob? job = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            job = _processor.GetJob(jobId);
            if (job is null) break;
            if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled) break;
            await Task.Delay(PollInterval, cancellationToken);
        }
        var summary = job is null
            ? $" [{label}: queued (job record evicted)]"
            : $" [{label}: {job.ProcessedFiles}/{requested} ok, {job.FailedFiles} failed]";
        return (summary, job);
    }

    /// <summary>
    /// The files a finished job processed successfully. Only a
    /// <see cref="JobStatus.Completed"/> job is known to have attempted every
    /// file, recording each failure in <see cref="ProcessingJob.Errors"/>
    /// under the path it was given. A failed, cancelled, unfinished or
    /// evicted job doesn't say which files went through, so none count.
    /// </summary>
    private static IReadOnlyList<string> SucceededFiles(IReadOnlyList<string> files, ProcessingJob? job)
    {
        if (job is not { Status: JobStatus.Completed }) return Array.Empty<string>();
        var failed = new HashSet<string>(job.Errors.Keys, StringComparer.OrdinalIgnoreCase);
        return files.Where(f => !failed.Contains(f)).ToList();
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
        public bool ReprocessModified { get; set; }
    }
}
