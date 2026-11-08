using System.Collections.Concurrent;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

public class SeriesMetadataRefreshJobService : ISeriesMetadataRefreshJobService
{
    private const int MaxRecentResults = 20;

    /// <summary>How long a finished job can still be read with <see cref="GetJob"/>.</summary>
    public static readonly TimeSpan FinishedJobRetention = TimeSpan.FromMinutes(30);

    /// <summary>Finished jobs kept, however recent, before the oldest are forgotten.</summary>
    public const int MaxFinishedJobs = 50;

    private readonly ISeriesMetadataCacheService _cache;
    private readonly IEventBroadcaster? _eventBroadcaster;
    private readonly IFileStoreService? _fileStore;
    private readonly IComicProcessorService? _processor;
    private readonly IOptionsMonitor<AppSettings>? _settings;
    private readonly ISeriesNameResolver? _seriesNameResolver;
    private readonly ILogger<SeriesMetadataRefreshJobService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Guid, MetadataRefreshJob> _jobs = new();

    // Finished jobs in the order they finished, so the oldest are evicted first.
    private readonly Queue<(Guid JobId, DateTime EndedAt)> _finishedJobs = new();
    private readonly object _finishedJobsSync = new();

    public SeriesMetadataRefreshJobService(
        ISeriesMetadataCacheService cache,
        ILogger<SeriesMetadataRefreshJobService> logger,
        IEventBroadcaster? eventBroadcaster = null,
        IFileStoreService? fileStore = null,
        IComicProcessorService? processor = null,
        TimeProvider? timeProvider = null,
        IOptionsMonitor<AppSettings>? settings = null,
        ISeriesNameResolver? seriesNameResolver = null)
    {
        _cache = cache;
        _logger = logger;
        _eventBroadcaster = eventBroadcaster;
        _fileStore = fileStore;
        _processor = processor;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _settings = settings;
        _seriesNameResolver = seriesNameResolver;
    }

    public Task<Guid> StartAsync(IEnumerable<string> seriesTitles, CancellationToken cancellationToken = default)
    {
        EvictFinishedJobs();

        var titles = (seriesTitles ?? Enumerable.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var jobId = Guid.NewGuid();
        var job = new MetadataRefreshJob
        {
            JobId = jobId,
            Status = JobStatus.Queued,
            Series = titles,
            TotalSeries = titles.Count,
            StartTime = _timeProvider.GetUtcNow().UtcDateTime
        };
        _jobs[jobId] = job;

        _ = Task.Run(() => RunJobAsync(job, cancellationToken), CancellationToken.None);
        return Task.FromResult(jobId);
    }

    public MetadataRefreshJob? GetJob(Guid jobId)
        => _jobs.TryGetValue(jobId, out var job) ? Clone(job) : null;

    public IEnumerable<MetadataRefreshJob> GetAllJobs()
        => _jobs.Values.Select(Clone).ToList();

    /// <summary>
    /// Forgets finished jobs older than <see cref="FinishedJobRetention"/> and the
    /// oldest finished jobs beyond <see cref="MaxFinishedJobs"/>, so the job table
    /// doesn't keep every job ever started. Running jobs are never evicted. Runs
    /// whenever a job is started, the only way the table grows.
    /// </summary>
    private void EvictFinishedJobs()
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - FinishedJobRetention;
        lock (_finishedJobsSync)
        {
            while (_finishedJobs.TryPeek(out var oldest)
                && (_finishedJobs.Count > MaxFinishedJobs || oldest.EndedAt <= cutoff))
            {
                _finishedJobs.Dequeue();
                _jobs.TryRemove(oldest.JobId, out _);
            }
        }
    }

    private async Task RunJobAsync(MetadataRefreshJob job, CancellationToken cancellationToken)
    {
        job.Status = JobStatus.Running;
        await BroadcastAsync(job);

        // Track titles whose cache refresh produced a usable canonical record,
        // along with their aliases, so we can re-tag the affected files at the
        // end of the run. We collect into a case-insensitive set keyed by every
        // title that should match a file's SeriesName.
        var titlesToRetag = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var title in job.Series)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    job.Status = JobStatus.Cancelled;
                    break;
                }

                job.CurrentSeries = title;
                string outcomeStatus;
                string? outcomeSource = null;
                try
                {
                    var record = await _cache.RefreshAsync(title, cancellationToken);
                    outcomeStatus = record.LookupStatus ?? "error";
                    outcomeSource = record.Source;
                    switch (record.LookupStatus)
                    {
                        case "success":
                        case "manual_match":
                            // A manually-matched series that successfully
                            // re-resolves to its user-selected match is a
                            // successful refresh outcome.
                            job.Successes++;
                            break;
                        case "not_found":
                            job.NotFound++;
                            break;
                        default:
                            job.Failures++;
                            break;
                    }

                    // Collect titles+aliases for the post-run file retag step.
                    // We always include the input title so files whose
                    // SeriesName still matches the pre-refresh value get
                    // re-tagged with the freshly resolved canonical name.
                    // A lookup that found nothing has no new name to apply, so
                    // its files keep their tags.
                    if (record.LookupStatus is "success" or "manual_match")
                    {
                        titlesToRetag.Add(title);
                        if (!string.IsNullOrWhiteSpace(record.CanonicalTitle))
                        {
                            titlesToRetag.Add(record.CanonicalTitle);
                        }
                        if (record.Aliases is { Count: > 0 })
                        {
                            titlesToRetag.UnionWith(record.Aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)));
                        }
                        if (record.UserAliases is { Count: > 0 })
                        {
                            titlesToRetag.UnionWith(record.UserAliases.Where(alias => !string.IsNullOrWhiteSpace(alias)));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    job.Status = JobStatus.Cancelled;
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogWarning(ex, "Metadata refresh failed for {SeriesTitle}", LoggingHelper.SanitizeForLog(title));
                    job.Failures++;
                    outcomeStatus = "error";
                }

                lock (job.RecentResults)
                {
                    job.RecentResults.Add(new MetadataRefreshOutcome
                    {
                        SeriesTitle = title,
                        Status = outcomeStatus,
                        Source = outcomeSource,
                        TimestampUtc = _timeProvider.GetUtcNow().UtcDateTime
                    });
                    while (job.RecentResults.Count > MaxRecentResults)
                    {
                        job.RecentResults.RemoveAt(0);
                    }
                }

                job.ProcessedSeries++;
                await BroadcastAsync(job);
            }

            if (job.Status == JobStatus.Running)
            {
                job.Status = JobStatus.Completed;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Metadata refresh job {JobId} failed", job.JobId);
            job.Status = JobStatus.Failed;
        }
        finally
        {
            lock (_finishedJobsSync)
            {
                job.EndTime = _timeProvider.GetUtcNow().UtcDateTime;
                _finishedJobs.Enqueue((job.JobId, job.EndTime.Value));
            }
            await BroadcastAsync(job);
        }

        // A refresh run looks up the canonical title AND every alias of each
        // series, so it can leave behind redundant "stale sibling" cache rows
        // (e.g. a not_found result keyed by an alias already owned by the
        // authoritative record). Prune them so they don't clutter the cache or
        // shadow the authoritative record's resolved name. Best-effort: a
        // failure here must not affect the job outcome or the retag step below.
        try
        {
            var removed = await _cache.CleanupStaleSiblingRecordsAsync(cancellationToken);
            if (removed > 0)
            {
                _logger.LogInformation(
                    "Removed {Count} stale series-metadata cache record(s) after refresh job {JobId}",
                    removed, job.JobId);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex,
                "Failed to clean up stale series-metadata cache records after refresh job {JobId}",
                job.JobId);
        }

        // After the cache has been refreshed for every requested title, push
        // the changes down to the individual comic files so each ComicInfo.xml
        // is rewritten to reflect the freshly resolved canonical series name
        // (and any other normalization driven by the cache record). This runs
        // outside the main try/catch so an indexing failure here cannot mark
        // the metadata-refresh job itself as failed — file retagging is a
        // best-effort downstream step and surfaces its own progress through
        // the standard processing-job pipeline.
        if (job.Status == JobStatus.Completed && titlesToRetag.Count > 0)
        {
            try
            {
                await QueueFileRetagAsync(job.JobId, titlesToRetag, cancellationToken);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex,
                    "Failed to queue per-file retag after metadata refresh job {JobId}",
                    job.JobId);
            }
        }
    }

    /// <summary>
    /// Queues a forced normalization of the tracked files that carry one of the
    /// just-refreshed titles (canonical title or alias) in <c>Metadata.Series</c>
    /// and still need rewriting, so their per-file ComicInfo.xml picks up the
    /// updated series metadata cache. No-op when the file store or processor
    /// services are not wired up (e.g. lightweight unit tests).
    /// </summary>
    /// <remarks>
    /// <c>ComicFiles.Metadata</c> caches every archive's tags, so a matching title
    /// no longer means "the user edited this file": after a refresh of the whole
    /// library most files match. Only the files whose cached <c>&lt;Series&gt;</c>
    /// differs from what normalization would write are queued, which keeps a refresh
    /// from opening and rewriting the whole library, and nothing is queued when the
    /// administrator has turned normalization off.
    /// </remarks>
    private async Task QueueFileRetagAsync(
        Guid sourceJobId,
        HashSet<string> titles,
        CancellationToken cancellationToken)
    {
        if (_fileStore is null || _processor is null) return;

        if (_settings?.CurrentValue.WatcherEnableNormalize == false)
        {
            _logger.LogWarning(
                "Metadata refresh job {JobId} will not rewrite per-file metadata because WatcherEnableNormalize is false.",
                sourceJobId);
            return;
        }

        var files = await _fileStore.GetAllFilesAsync(cancellationToken);
        var matchedFiles = files
            .Where(f => !string.IsNullOrWhiteSpace(f.Metadata?.Series)
                && titles.Contains(f.Metadata!.Series!)
                && !string.IsNullOrWhiteSpace(f.FilePath))
            .DistinctBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matchedFiles.Count == 0)
        {
            _logger.LogInformation(
                "Metadata refresh job {JobId} completed; no tracked files matched the refreshed series titles, nothing to retag.",
                sourceJobId);
            return;
        }

        var pathsToRetag = await SelectFilesNeedingRetagAsync(matchedFiles, cancellationToken);
        if (pathsToRetag.Count == 0)
        {
            _logger.LogInformation(
                "Metadata refresh job {JobId} completed; all {FileCount} matching files already carry the resolved series name, nothing to retag.",
                sourceJobId, matchedFiles.Count);
            return;
        }

        // Force re-normalization so files already flagged as normalized still
        // get rewritten with the updated series name pulled from the cache.
        var processJobId = await _processor.NormalizeFilesAsync(pathsToRetag, forceReprocess: true, cancellationToken);
        _logger.LogInformation(
            "Metadata refresh job {JobId} queued normalization job {ProcessJobId} for {FileCount} files to propagate updated series metadata.",
            sourceJobId, processJobId, pathsToRetag.Count);
    }

    /// <summary>
    /// The files of <paramref name="candidates"/> whose cached <c>&lt;Series&gt;</c> differs
    /// from the name normalization would write, decided without opening any archive. The
    /// name depends only on the file's folder and its current tag, so each combination is
    /// resolved once. Without a resolver every candidate is returned.
    /// </summary>
    private async Task<List<string>> SelectFilesNeedingRetagAsync(
        IReadOnlyList<ComicFile> candidates,
        CancellationToken cancellationToken)
    {
        if (_seriesNameResolver is null)
        {
            return candidates.Select(f => f.FilePath).ToList();
        }

        var resolvedNames = new Dictionary<(string? Folder, string? Series), string?>();
        var paths = new List<string>();
        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = (Path.GetDirectoryName(file.FilePath), file.Metadata?.Series);
            if (!resolvedNames.TryGetValue(key, out var resolved))
            {
                var resolution = await _seriesNameResolver.ResolveAsync(
                    file.FilePath, file.Metadata, mutateCache: false, cancellationToken);
                resolved = resolution.ResolvedSeries;
                resolvedNames[key] = resolved;
            }

            if (!string.Equals(file.Metadata?.Series, resolved, StringComparison.Ordinal))
            {
                paths.Add(file.FilePath);
            }
        }

        return paths;
    }

    // Best-effort: a failed broadcast (thrown or a faulted task) is logged and
    // never changes the job's outcome.
    private async Task BroadcastAsync(MetadataRefreshJob job)
    {
        if (_eventBroadcaster is null) return;
        try
        {
            await _eventBroadcaster.BroadcastJobUpdateAsync(
                job.JobId,
                job.Status.ToString().ToLowerInvariant(),
                job.ProcessedSeries,
                job.TotalSeries,
                job.Successes,
                job.Failures);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Failed to broadcast metadata refresh job update {JobId}", job.JobId);
        }
    }

    private static MetadataRefreshJob Clone(MetadataRefreshJob job)
    {
        List<MetadataRefreshOutcome> recentClone;
        lock (job.RecentResults)
        {
            recentClone = job.RecentResults
                .Select(r => new MetadataRefreshOutcome
                {
                    SeriesTitle = r.SeriesTitle,
                    Status = r.Status,
                    Source = r.Source,
                    TimestampUtc = r.TimestampUtc
                })
                .ToList();
        }

        return new MetadataRefreshJob
        {
            JobId = job.JobId,
            Status = job.Status,
            Series = job.Series.ToList(),
            TotalSeries = job.TotalSeries,
            ProcessedSeries = job.ProcessedSeries,
            Successes = job.Successes,
            NotFound = job.NotFound,
            Failures = job.Failures,
            CurrentSeries = job.CurrentSeries,
            StartTime = job.StartTime,
            EndTime = job.EndTime,
            RecentResults = recentClone
        };
    }
}
