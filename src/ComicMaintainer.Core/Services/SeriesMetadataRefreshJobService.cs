using System.Collections.Concurrent;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

public class SeriesMetadataRefreshJobService : ISeriesMetadataRefreshJobService
{
    private readonly ISeriesMetadataCacheService _cache;
    private readonly IEventBroadcaster? _eventBroadcaster;
    private readonly ILogger<SeriesMetadataRefreshJobService> _logger;
    private readonly ConcurrentDictionary<Guid, MetadataRefreshJob> _jobs = new();

    public SeriesMetadataRefreshJobService(
        ISeriesMetadataCacheService cache,
        ILogger<SeriesMetadataRefreshJobService> logger,
        IEventBroadcaster? eventBroadcaster = null)
    {
        _cache = cache;
        _logger = logger;
        _eventBroadcaster = eventBroadcaster;
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
                try
                {
                    var record = await _cache.RefreshAsync(title, cancellationToken);
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
                }
                catch (OperationCanceledException)
                {
                    job.Status = JobStatus.Cancelled;
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Metadata refresh failed for {SeriesTitle}", title);
                    job.Failures++;
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

    private static MetadataRefreshJob Clone(MetadataRefreshJob job) => new()
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
        EndTime = job.EndTime
    };
}
