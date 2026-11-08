using System.Text.Json;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Stores batch processing jobs in the database so they survive a restart.
/// </summary>
public class JobStateStore : IJobStateStore
{
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly ILogger<JobStateStore> _logger;

    private static readonly JsonSerializerOptions SerializerOptions = new();

    public JobStateStore(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        ILogger<JobStateStore> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task SaveAsync(ProcessingJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var status = job.Status.ToString();
        var operationName = Truncate(job.OperationName, 128);
        var errorsJson = JsonSerializer.Serialize(job.Errors, SerializerOptions);
        var currentFile = TruncateOrNull(job.CurrentFile, 2048);
        var updatedAt = DateTime.UtcNow;

        // A job's file list is fixed when the job is created and can run to megabytes, so it is
        // stored once, with the row, and a progress write neither reads nor rewrites it.
        var updated = await db.ProcessingJobs
            .Where(e => e.JobId == job.JobId)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(e => e.Status, status)
                    .SetProperty(e => e.OperationName, operationName)
                    .SetProperty(e => e.ErrorsJson, errorsJson)
                    .SetProperty(e => e.TotalFiles, job.TotalFiles)
                    .SetProperty(e => e.ProcessedFiles, job.ProcessedFiles)
                    .SetProperty(e => e.FailedFiles, job.FailedFiles)
                    .SetProperty(e => e.StartTime, job.StartTime)
                    .SetProperty(e => e.EndTime, job.EndTime)
                    .SetProperty(e => e.CurrentFile, currentFile)
                    .SetProperty(e => e.UpdatedAt, updatedAt),
                cancellationToken);

        if (updated > 0)
        {
            return;
        }

        db.ProcessingJobs.Add(new ProcessingJobEntity
        {
            JobId = job.JobId,
            Status = status,
            OperationName = operationName,
            FilesJson = JsonSerializer.Serialize(job.Files, SerializerOptions),
            ErrorsJson = errorsJson,
            TotalFiles = job.TotalFiles,
            ProcessedFiles = job.ProcessedFiles,
            FailedFiles = job.FailedFiles,
            StartTime = job.StartTime,
            EndTime = job.EndTime,
            CurrentFile = currentFile,
            UpdatedAt = updatedAt
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProcessingJob>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var entities = await db.ProcessingJobs
            .AsNoTracking()
            .OrderByDescending(e => e.StartTime)
            .ToListAsync(cancellationToken);

        return entities.Select(ToJob).ToList();
    }

    public async Task<bool> DeleteAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await db.ProcessingJobs
            .FirstOrDefaultAsync(e => e.JobId == jobId, cancellationToken);

        if (entity == null)
        {
            return false;
        }

        db.ProcessingJobs.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ProcessingJob>> MarkInterruptedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var queued = JobStatus.Queued.ToString();
        var running = JobStatus.Running.ToString();

        var entities = await db.ProcessingJobs
            .Where(e => e.Status == queued || e.Status == running)
            .ToListAsync(cancellationToken);

        if (entities.Count == 0)
        {
            return Array.Empty<ProcessingJob>();
        }

        var now = DateTime.UtcNow;
        foreach (var entity in entities)
        {
            entity.Status = JobStatus.Interrupted.ToString();
            // The job stopped when the process did. The last progress write is the closest
            // known time, so prefer it over "now", which would otherwise include all the
            // downtime between the crash and this restart.
            entity.EndTime = entity.UpdatedAt == default ? now : entity.UpdatedAt;
            entity.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "Marked {Count} job(s) as interrupted; they were still in flight when the process stopped",
            entities.Count);

        return entities.Select(ToJob).ToList();
    }

    public async Task<int> PruneAsync(DateTime cutoff, int keepMostRecent, CancellationToken cancellationToken = default)
    {
        if (keepMostRecent < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keepMostRecent), "Retention count cannot be negative.");
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var queued = JobStatus.Queued.ToString();
        var running = JobStatus.Running.ToString();

        // Only terminal jobs are eligible; an in-flight job must never be pruned out from
        // under the running batch, however old its start time.
        var terminal = db.ProcessingJobs.Where(e => e.Status != queued && e.Status != running);

        var keepIds = await terminal
            .OrderByDescending(e => e.StartTime)
            .Take(keepMostRecent)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

        var stale = await terminal
            .Where(e => e.StartTime < cutoff && !keepIds.Contains(e.Id))
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return 0;
        }

        db.ProcessingJobs.RemoveRange(stale);
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Pruned {Count} completed job record(s) older than {Cutoff:o}", stale.Count, cutoff);
        return stale.Count;
    }

    private ProcessingJob ToJob(ProcessingJobEntity entity)
    {
        return new ProcessingJob
        {
            JobId = entity.JobId,
            // Only the exact names Apply writes are valid. Enum.TryParse alone also accepts
            // numbers, whitespace and comma-separated names, so "1" would restore as a Running job
            // and "42" as an undefined status: MarkInterruptedAsync matches exact names and never
            // sweeps them, leaving a job that can be neither deleted nor cancelled. Enum.IsDefined
            // with a string matches declared names exactly (ordinal, case-sensitive) and nothing else.
            Status = entity.Status is not null
                && Enum.IsDefined(typeof(JobStatus), entity.Status)
                && Enum.TryParse<JobStatus>(entity.Status, out var status)
                ? status
                : JobStatus.Interrupted,
            OperationName = entity.OperationName,
            Files = Deserialize<List<string>>(entity.FilesJson, entity.JobId, nameof(entity.FilesJson)) ?? new List<string>(),
            Errors = Deserialize<Dictionary<string, string>>(entity.ErrorsJson, entity.JobId, nameof(entity.ErrorsJson)) ?? new Dictionary<string, string>(),
            TotalFiles = entity.TotalFiles,
            ProcessedFiles = entity.ProcessedFiles,
            FailedFiles = entity.FailedFiles,
            StartTime = entity.StartTime,
            EndTime = entity.EndTime,
            CurrentFile = entity.CurrentFile
        };
    }

    private T? Deserialize<T>(string? json, Guid jobId, string field) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            // A corrupt payload should degrade to an empty collection rather than take down
            // job listing for every other job.
            _logger.LogWarning(ex, "Could not deserialize {Field} for job {JobId}", field, jobId);
            return null;
        }
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string? TruncateOrNull(string? value, int maxLength)
    {
        if (value == null)
        {
            return null;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
