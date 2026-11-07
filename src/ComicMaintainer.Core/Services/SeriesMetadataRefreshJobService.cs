using System.Collections.Concurrent;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

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
        TimeProvider? timeProvider = null)
    {
        _cache = cache;
        _logger = logger;
        _eventBroadcaster = eventBroadcaster;
        _fileStore = fileStore;
        _processor = processor;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

        // Series keys of every series whose refresh produced a usable match,
        // so the files of those series can be re-tagged at the end of the run.
        var seriesKeysToRetag = new HashSet<string>(StringComparer.Ordinal);

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
                string? outcomeError = null;
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
                            // Retag the files of the series: those under the
                            // requested title (typically the folder name) and
                            // under any title of the refreshed record, so the
                            // freshly resolved name reaches each file.
                            seriesKeysToRetag.UnionWith(SeriesFileMatcher.KeysOf(record, title));
                            break;
                        case "not_found":
                            // Nothing new is known, so the files keep their tags.
                            job.NotFound++;
                            break;
                        default:
                            job.Failures++;
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    job.Status = JobStatus.Cancelled;
                    break;
                }
                catch (ExternalMetadataUnavailableException ex)
                {
                    // The provider could not answer: the record was kept as it
                    // was, and the series counts as failed, not as not found.
                    _logger.LogWarning("Metadata refresh failed for {SeriesTitle}: {Reason}",
                        LoggingHelper.SanitizeForLog(title), LoggingHelper.SanitizeForLog(ex.Message));
                    job.Failures++;
                    outcomeStatus = "error";
                    outcomeError = ex.Message;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogWarning(ex, "Metadata refresh failed for {SeriesTitle}", LoggingHelper.SanitizeForLog(title));
                    job.Failures++;
                    outcomeStatus = "error";
                    outcomeError = "Refresh failed; see the server log";
                }

                lock (job.RecentResults)
                {
                    job.RecentResults.Add(new MetadataRefreshOutcome
                    {
                        SeriesTitle = title,
                        Status = outcomeStatus,
                        Source = outcomeSource,
                        Error = outcomeError,
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
        if (job.Status == JobStatus.Completed && seriesKeysToRetag.Count > 0)
        {
            try
            {
                await QueueFileRetagAsync(job.JobId, seriesKeysToRetag, cancellationToken);
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
    /// Enumerate every tracked comic file of the just-refreshed series (by
    /// <see cref="SeriesFileMatcher"/>: its tracked <c>&lt;Series&gt;</c> or its
    /// folder-derived series name has one of <paramref name="seriesKeys"/>) and
    /// queue them for normalization so their per-file ComicInfo.xml is
    /// rewritten using the updated series metadata cache. No-op when the file
    /// store or processor services are not wired up (e.g. lightweight unit tests).
    /// </summary>
    private async Task QueueFileRetagAsync(
        Guid sourceJobId,
        IReadOnlySet<string> seriesKeys,
        CancellationToken cancellationToken)
    {
        if (_fileStore is null || _processor is null) return;

        var files = await _fileStore.GetAllFilesAsync(cancellationToken);
        var matchedPaths = SeriesFileMatcher.SelectFilePaths(files, seriesKeys);

        if (matchedPaths.Count == 0)
        {
            _logger.LogInformation(
                "Metadata refresh job {JobId} completed; no tracked files matched the refreshed series titles, nothing to retag.",
                sourceJobId);
            return;
        }

        // Force re-normalization so files already flagged as normalized still
        // get rewritten with the updated series name pulled from the cache.
        var processJobId = await _processor.NormalizeFilesAsync(matchedPaths, forceReprocess: true, cancellationToken);
        _logger.LogInformation(
            "Metadata refresh job {JobId} queued normalization job {ProcessJobId} for {FileCount} files to propagate updated series metadata.",
            sourceJobId, processJobId, matchedPaths.Count);
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
                    Error = r.Error,
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
