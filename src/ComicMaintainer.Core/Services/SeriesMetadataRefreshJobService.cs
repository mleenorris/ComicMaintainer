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

        // Series keys of every series whose refresh changed what its files
        // should say, so those files can be re-tagged at the end of the run.
        var seriesKeysToRetag = new HashSet<string>(StringComparer.Ordinal);

        // What refreshing each record concluded, by record key. A series card's
        // refresh lists the canonical title and every alias, all of which
        // belong to one record: it is looked up once and the rest share the
        // answer, instead of each alias re-querying the providers.
        var refreshedRecords = new Dictionary<string, RefreshResult>(StringComparer.Ordinal);

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
                RefreshResult result;
                SeriesMetadataCacheRecord? before = null;
                try
                {
                    before = await _cache.GetByTitleAsync(title, cancellationToken);
                    if (before is not null && refreshedRecords.TryGetValue(before.NormalizedKey, out var earlier))
                    {
                        result = earlier;
                        if (result.Retag && SeriesKey.TryNormalize(title) is { } titleKey)
                        {
                            seriesKeysToRetag.Add(titleKey);
                        }
                    }
                    else
                    {
                        var record = await _cache.RefreshAsync(title, cancellationToken);
                        result = new RefreshResult(record.LookupStatus ?? "error", record.Source, null, RefreshChangedTheSeries(before, record));
                        Remember(refreshedRecords, record, result);
                        if (result.Retag)
                        {
                            // Retag the files of the series: those under the
                            // requested title (typically the folder name) and
                            // under any title of the refreshed record, so the
                            // freshly resolved name reaches each file.
                            seriesKeysToRetag.UnionWith(SeriesFileMatcher.KeysOf(record, title));
                        }
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
                    result = new RefreshResult("error", null, ex.Message, Retag: false);
                    Remember(refreshedRecords, before, result);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogWarning(ex, "Metadata refresh failed for {SeriesTitle}", LoggingHelper.SanitizeForLog(title));
                    result = new RefreshResult("error", null, "Refresh failed; see the server log", Retag: false);
                    Remember(refreshedRecords, before, result);
                }

                switch (result.Status)
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

                lock (job.RecentResults)
                {
                    job.RecentResults.Add(new MetadataRefreshOutcome
                    {
                        SeriesTitle = title,
                        Status = result.Status,
                        Source = result.Source,
                        Error = result.Error,
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

        // A refresh run can leave behind redundant "stale sibling" cache rows
        // (e.g. a not_found result keyed by a title that was not yet known to
        // be an alias of the authoritative record, or rows from earlier
        // versions). Prune them so they don't clutter the cache or
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

    /// <summary>Remembers a refresh's result, so the record's other titles do not repeat it.</summary>
    private static void Remember(
        Dictionary<string, RefreshResult> refreshedRecords,
        SeriesMetadataCacheRecord? record,
        RefreshResult result)
    {
        if (!string.IsNullOrEmpty(record?.NormalizedKey))
        {
            refreshedRecords[record.NormalizedKey] = result;
        }
    }

    /// <summary>
    /// True when the refresh left a usable match that differs from what was
    /// cached before it, so the series' files have something new to pick up.
    /// A refresh that confirms the existing match re-tags nothing: re-opening
    /// every archive of a matched library would achieve nothing.
    /// </summary>
    private static bool RefreshChangedTheSeries(SeriesMetadataCacheRecord? before, SeriesMetadataCacheRecord after)
    {
        if (!IsMatched(after.LookupStatus))
        {
            return false;
        }

        return before is null
               || !IsMatched(before.LookupStatus)
               || before.MetadataVersion != after.MetadataVersion
               || !string.Equals(before.CanonicalTitle, after.CanonicalTitle, StringComparison.Ordinal);

        static bool IsMatched(string? status) => status is "success" or "manual_match";
    }

    /// <summary>What refreshing one record concluded, shared by its other titles in the same run.</summary>
    private sealed record RefreshResult(string Status, string? Source, string? Error, bool Retag);

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
