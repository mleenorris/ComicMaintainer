using System.Collections.Concurrent;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

public class SeriesMetadataRefreshJobService : ISeriesMetadataRefreshJobService
{
    private const int MaxRecentResults = 20;

    private readonly ISeriesMetadataCacheService _cache;
    private readonly IEventBroadcaster? _eventBroadcaster;
    private readonly IFileStoreService? _fileStore;
    private readonly IComicProcessorService? _processor;
    private readonly ILogger<SeriesMetadataRefreshJobService> _logger;
    private readonly ConcurrentDictionary<Guid, MetadataRefreshJob> _jobs = new();

    public SeriesMetadataRefreshJobService(
        ISeriesMetadataCacheService cache,
        ILogger<SeriesMetadataRefreshJobService> logger,
        IEventBroadcaster? eventBroadcaster = null,
        IFileStoreService? fileStore = null,
        IComicProcessorService? processor = null)
    {
        _cache = cache;
        _logger = logger;
        _eventBroadcaster = eventBroadcaster;
        _fileStore = fileStore;
        _processor = processor;
    }

    public Task<Guid> StartAsync(IEnumerable<string> seriesTitles, CancellationToken cancellationToken = default)
    {
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
            StartTime = DateTime.UtcNow
        };
        _jobs[jobId] = job;

        _ = Task.Run(() => RunJobAsync(job, cancellationToken), CancellationToken.None);
        return Task.FromResult(jobId);
    }

    public MetadataRefreshJob? GetJob(Guid jobId)
        => _jobs.TryGetValue(jobId, out var job) ? Clone(job) : null;

    public IEnumerable<MetadataRefreshJob> GetAllJobs()
        => _jobs.Values.Select(Clone).ToList();

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
                string outcomeStatus = "error";
                string? outcomeSource = null;
                try
                {
                    var record = await _cache.RefreshAsync(title, cancellationToken);
                    outcomeStatus = record.LookupStatus ?? "error";
                    outcomeSource = record.Source;
                    switch (record.LookupStatus)
                    {
                        case "success":
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
                    titlesToRetag.Add(title);
                    if (!string.IsNullOrWhiteSpace(record.CanonicalTitle))
                    {
                        titlesToRetag.Add(record.CanonicalTitle);
                    }
                    if (record.Aliases is { Count: > 0 })
                    {
                        foreach (var alias in record.Aliases)
                        {
                            if (!string.IsNullOrWhiteSpace(alias)) titlesToRetag.Add(alias);
                        }
                    }
                    if (record.UserAliases is { Count: > 0 })
                    {
                        foreach (var alias in record.UserAliases)
                        {
                            if (!string.IsNullOrWhiteSpace(alias)) titlesToRetag.Add(alias);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    job.Status = JobStatus.Cancelled;
                    break;
                }
                catch (Exception ex)
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
                        TimestampUtc = DateTime.UtcNow
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metadata refresh job {JobId} failed", job.JobId);
            job.Status = JobStatus.Failed;
        }
        finally
        {
            job.EndTime = DateTime.UtcNow;
            await BroadcastAsync(job);
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
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to queue per-file retag after metadata refresh job {JobId}",
                    job.JobId);
            }
        }
    }

    /// <summary>
    /// Enumerate every tracked comic file whose <c>Metadata.Series</c> matches
    /// any of the just-refreshed titles (canonical title or alias) and queue
    /// them for normalization so their per-file ComicInfo.xml is rewritten
    /// using the updated series metadata cache. No-op when the file store or
    /// processor services are not wired up (e.g. lightweight unit tests).
    /// </summary>
    private async Task QueueFileRetagAsync(
        Guid sourceJobId,
        HashSet<string> titles,
        CancellationToken cancellationToken)
    {
        if (_fileStore is null || _processor is null) return;

        var files = await _fileStore.GetAllFilesAsync(cancellationToken);
        var matchedPaths = files
            .Where(f => !string.IsNullOrWhiteSpace(f.Metadata?.Series)
                && titles.Contains(f.Metadata!.Series!))
            .Select(f => f.FilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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

    private Task BroadcastAsync(MetadataRefreshJob job)
    {
        if (_eventBroadcaster is null) return Task.CompletedTask;
        try
        {
            return _eventBroadcaster.BroadcastJobUpdateAsync(
                job.JobId,
                job.Status.ToString().ToLowerInvariant(),
                job.ProcessedSeries,
                job.TotalSeries,
                job.Successes,
                job.Failures);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to broadcast metadata refresh job update {JobId}", job.JobId);
            return Task.CompletedTask;
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
