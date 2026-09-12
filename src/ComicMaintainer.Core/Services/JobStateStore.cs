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

        var entity = await db.ProcessingJobs
            .FirstOrDefaultAsync(e => e.JobId == job.JobId, cancellationToken);

        if (entity == null)
        {
            entity = new ProcessingJobEntity { JobId = job.JobId };
            db.ProcessingJobs.Add(entity);
        }

        Apply(job, entity);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Job persistence is best-effort observability, never a reason to fail the batch
            // the user actually asked for. Losing a progress write only means the job is
            // reported as interrupted if the process dies before the next successful write.
            _logger.LogWarning(ex, "Failed to persist state for job {JobId}", job.JobId);
        }
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

    private static void Apply(ProcessingJob job, ProcessingJobEntity entity)
    {
        entity.Status = job.Status.ToString();
        entity.OperationName = Truncate(job.OperationName, 128);
        entity.FilesJson = JsonSerializer.Serialize(job.Files, SerializerOptions);
        entity.ErrorsJson = JsonSerializer.Serialize(job.Errors, SerializerOptions);
        entity.TotalFiles = job.TotalFiles;
        entity.ProcessedFiles = job.ProcessedFiles;
        entity.FailedFiles = job.FailedFiles;
        entity.StartTime = job.StartTime;
        entity.EndTime = job.EndTime;
        entity.CurrentFile = TruncateOrNull(job.CurrentFile, 2048);
        entity.UpdatedAt = DateTime.UtcNow;
    }

    private ProcessingJob ToJob(ProcessingJobEntity entity)
    {
        return new ProcessingJob
        {
            JobId = entity.JobId,
            Status = Enum.TryParse<JobStatus>(entity.Status, out var status) ? status : JobStatus.Interrupted,
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
