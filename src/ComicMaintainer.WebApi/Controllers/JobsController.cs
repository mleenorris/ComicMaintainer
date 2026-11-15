using System.Text.Json.Serialization;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class JobsController : ControllerBase
{
    /// <summary>
    /// The error returned when a batch request names a file outside the watched and duplicate directories.
    /// </summary>
    public const string OutsideAllowedDirectoryError = "One or more file paths are outside the allowed directory";

    private const string NoFilesSpecifiedError = "No files specified";

    /// <summary>
    /// The batch operations, by the first half of their route: <c>POST /api/jobs/{operation}-{scope}</c>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, BatchOperation> BatchOperations =
        new Dictionary<string, BatchOperation>(StringComparer.OrdinalIgnoreCase)
        {
            ["process"] = new(BatchScope.Any, batch => batch.Processor.ProcessFilesAsync(batch.Files, batch.ForceReprocess)),
            ["rename"] = new(BatchScope.Any, batch => batch.Processor.RenameFilesAsync(batch.Files, batch.ForceReprocess)),
            ["normalize"] = new(BatchScope.Any, batch => batch.Processor.NormalizeFilesAsync(batch.Files, batch.ForceReprocess)),
            ["remove-metadata"] = new(BatchScope.Selected | BatchScope.All, batch => batch.Processor.RemoveMetadataFromFilesAsync(batch.Files)),
            ["update-metadata"] = new(
                BatchScope.Selected,
                batch => batch.Processor.UpdateMetadataAsync(batch.Files, batch.Request.Metadata!, MetadataFields(batch.Request)),
                ValidateMetadataEdit),
            ["delete"] = new(BatchScope.Selected, batch => batch.Processor.DeleteFilesAsync(batch.Files), ActsOnEntry: true),
        };

    private readonly IComicProcessorService _processor;
    private readonly IBatchJobRegistry _jobRegistry;
    private readonly IFileStoreService _fileStore;
    private readonly ILogger<JobsController> _logger;
    private readonly IOptionsMonitor<AppSettings> _settings;

    public JobsController(
        IComicProcessorService processor,
        IBatchJobRegistry jobRegistry,
        IFileStoreService fileStore,
        ILogger<JobsController> logger,
        IOptionsMonitor<AppSettings> settings)
    {
        _processor = processor;
        _jobRegistry = jobRegistry;
        _fileStore = fileStore;
        _logger = logger;
        _settings = settings;
    }

    /// <summary>
    /// A job's progress. The per-file <c>results</c> (one entry for every file of the job, so
    /// megabytes for a whole-library job) are included only when asked for with
    /// <c>?include=results</c>: polling for progress needs only the counts.
    /// </summary>
    [HttpGet("{jobId}")]
    public ActionResult<object> GetJob(Guid jobId, [FromQuery] string? include = null)
    {
        var job = _jobRegistry.GetJob(jobId);
        if (job == null)
            return NotFound();

        // Problems a job's post-loop hook reported for things that were
        // not tracked items (IBatchJobReport), e.g. a source folder that
        // folder combine had to keep. They are not results, so they
        // never change what the job's item counts or results say.
        var trackedItems = job.Errors.Count == 0 ? null : new HashSet<string>(job.Files, StringComparer.Ordinal);
        var reportedProblems = job.Errors
            .Where(entry => !trackedItems!.Contains(entry.Key))
            .Select(entry => new
            {
                subject = entry.Key,
                message = entry.Value
            })
            .ToList();

        // Return in snake_case format expected by frontend
        var status = new Dictionary<string, object?>
        {
            ["job_id"] = job.JobId.ToString(),
            ["status"] = job.Status.ToString().ToLower(),
            ["total_items"] = job.TotalFiles,
            ["processed_items"] = job.ProcessedFiles,
            ["failed_items"] = job.FailedFiles,
            ["current_file"] = job.CurrentFile,
            ["start_time"] = job.StartTime,
            ["end_time"] = job.EndTime,
        };

        if (IncludesResults(include))
        {
            // The files the job has been through, in order; one not in the error list succeeded.
            var processedFiles = job.Files.Take(job.ProcessedFiles + job.FailedFiles).ToHashSet(StringComparer.Ordinal);
            status["results"] = job.Files.Select(f => new
            {
                file = f,
                success = processedFiles.Contains(f) && !job.Errors.ContainsKey(f),
                error = job.Errors.GetValueOrDefault(f)
            }).ToList();
        }

        status["reported_problems"] = reportedProblems;
        return Ok(status);
    }

    // `include` is a comma-separated list, so other parts can be added the same way.
    private static bool IncludesResults(string? include) =>
        include is not null
        && include.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains("results", StringComparer.OrdinalIgnoreCase);

    [HttpGet("~/api/active-job")]
    public ActionResult<object> GetActiveJob()
    {
        // After restart, in-flight jobs are reconciled to Interrupted and are no longer
        // "active", but the UI still needs one last status to explain why work stopped.
        var job = _jobRegistry.GetActiveJob()
            ?? _jobRegistry.GetJobSummaries().FirstOrDefault(j => j.Status == JobStatus.Interrupted);

        if (job == null)
            return Ok(new { active = false });

        // Return in snake_case format expected by frontend
        return Ok(new
        {
            job_id = job.JobId.ToString(),
            job_title = DetermineJobTitle(job),
            status = job.Status.ToString().ToLower(),
            total_items = job.TotalFiles,
            processed_items = job.ProcessedFiles,
            failed_items = job.FailedFiles,
            current_file = job.CurrentFile,
            start_time = job.StartTime,
            end_time = job.EndTime
        });
    }

    private static string DetermineJobTitle(ProcessingJobSummary job)
    {
        // Prefer the recorded operation, which survives a restart; fall back to the older
        // status-only wording for jobs persisted before OperationName was tracked.
        var operation = string.IsNullOrWhiteSpace(job.OperationName) ? null : job.OperationName;

        return job.Status switch
        {
            JobStatus.Queued when job.ProcessedFiles == 0 => "Processing Files...",
            JobStatus.Running => $"Processing {job.TotalFiles} files...",
            JobStatus.Completed => $"Completed {job.TotalFiles} files",
            JobStatus.Failed => "Processing Failed",
            JobStatus.Cancelled => "Processing Cancelled",
            JobStatus.Interrupted => operation == null
                ? "Interrupted by restart"
                : $"{operation} interrupted by restart",
            _ => "Processing..."
        };
    }

    /// <summary>
    /// Starts a batch job: one of the <see cref="BatchOperations"/> over the files a scope
    /// selects. The routes this serves are the operation and scope joined by a hyphen, such as
    /// <c>POST /api/jobs/process-all</c> or <c>POST /api/jobs/remove-metadata-selected</c>.
    /// </summary>
    /// <param name="operation">What to do: process, rename, normalize, remove-metadata, update-metadata or delete.</param>
    /// <param name="scope">Which files: selected, all or unmarked.</param>
    /// <param name="request">The selected files and options. Not needed for the all and unmarked scopes.</param>
    /// <param name="forceReprocess">Reprocess files already marked as done; the body can say the same.</param>
    /// <param name="cancellationToken">Cancels selecting the files. The job itself outlives the request.</param>
    [HttpPost("{operation}-{scope}")]
    public async Task<ActionResult<BatchJobResponse>> StartBatchJob(
        string operation,
        string scope,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] BatchJobRequest? request,
        [FromQuery] bool forceReprocess = false,
        CancellationToken cancellationToken = default)
    {
        if (!BatchOperations.TryGetValue(operation, out var batchOperation)
            || !TryParseScope(scope, out var batchScope)
            || !batchOperation.Scopes.HasFlag(batchScope))
        {
            return NotFound();
        }

        request ??= new BatchJobRequest();

        var (files, selectionError) = await SelectFilesAsync(batchOperation, batchScope, request, cancellationToken);
        if (selectionError != null)
        {
            return BadRequest(new { error = selectionError });
        }

        if (batchOperation.Validate?.Invoke(request) is { } validationError)
        {
            return BadRequest(new { error = validationError });
        }

        if (files.Count == 0)
        {
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("Batch {Operation}-{Scope}: no files to act on"),
                LoggingHelper.SanitizeForLog(operation), LoggingHelper.SanitizeForLog(scope));
            return Ok(new BatchJobResponse(Guid.Empty.ToString(), 0));
        }

        var jobId = await batchOperation.Start(new BatchInvocation(_processor, files, request, forceReprocess || request.ForceReprocess));

        _logger.LogInformation(
            LoggingHelper.WithWebsitePrefix("Batch {Operation}-{Scope}: job {JobId} started for {TotalFiles} file(s)"),
            LoggingHelper.SanitizeForLog(operation), LoggingHelper.SanitizeForLog(scope), jobId, files.Count);

        return Ok(new BatchJobResponse(jobId.ToString(), files.Count));
    }

    /// <summary>
    /// The one selection rule set of every batch operation. The library-wide scopes never
    /// include files marked as duplicates; files the caller names are taken as named, which is
    /// how the duplicates view deletes them, but must be in the library.
    /// </summary>
    private async Task<(IReadOnlyList<string> Files, string? Error)> SelectFilesAsync(
        BatchOperation operation,
        BatchScope scope,
        BatchJobRequest request,
        CancellationToken cancellationToken)
    {
        switch (scope)
        {
            case BatchScope.All:
                var allFiles = await _fileStore.GetAllFilesAsync(cancellationToken);
                return (allFiles.Where(f => !f.IsDuplicate).Select(f => f.FilePath).ToList(), null);

            case BatchScope.Unmarked:
                // The "unprocessed" filter already leaves out duplicates.
                var unprocessed = await _fileStore.GetFilteredFilesAsync("unprocessed", cancellationToken);
                return (unprocessed.Select(f => f.FilePath).ToList(), null);

            default:
                var named = (request.Files ?? new List<string>())
                    .Where(file => !string.IsNullOrWhiteSpace(file))
                    .ToList();
                if (named.Count == 0)
                {
                    return (named, NoFilesSpecifiedError);
                }

                return (named, FindPathOutsideLibrary(named, operation) == null ? null : OutsideAllowedDirectoryError);
        }
    }

    /// <summary>
    /// The first of <paramref name="files"/> that the <paramref name="operation"/>'s job would
    /// refuse to touch, or null when it would accept them all. This is the check the job makes
    /// per file, so no request is refused over a file its job would have taken, and rejecting
    /// the whole request here stops any file of it being acted on.
    /// </summary>
    private string? FindPathOutsideLibrary(IEnumerable<string> files, BatchOperation operation)
    {
        var settings = _settings.CurrentValue;

        // One instance for the whole request, since a selection can be the entire library.
        var roots = new LibraryRoots(settings.WatchedDirectory, settings.DuplicateDirectory);
        var outside = files.FirstOrDefault(file => operation.ActsOnEntry
            ? !roots.TryResolveEntryWithin(file, out _)
            : !roots.TryResolveWithin(file, out _));

        if (outside != null)
        {
            _logger.LogWarning(
                LoggingHelper.WithWebsitePrefix("Batch request names a file outside the allowed directories: {FilePath}"),
                LoggingHelper.SanitizePathForLog(outside));
        }

        return outside;
    }

    private static bool TryParseScope(string scope, out BatchScope parsed)
    {
        parsed = scope.ToLowerInvariant() switch
        {
            "selected" => BatchScope.Selected,
            "all" => BatchScope.All,
            "unmarked" => BatchScope.Unmarked,
            _ => BatchScope.None
        };
        return parsed != BatchScope.None;
    }

    private static string? ValidateMetadataEdit(BatchJobRequest request)
    {
        if (request.Metadata == null)
        {
            return "No metadata specified";
        }

        if (request.Fields is not null && !ComicMetadataFields.TryParse(request.Fields, out _, out var unknownField))
        {
            return $"Unknown metadata field: {unknownField}";
        }

        return MetadataFields(request) == ComicMetadataFieldFlags.None ? "No metadata fields specified" : null;
    }

    /// <summary>
    /// Fields lists the tags to apply; a listed field without a value is cleared. Without it,
    /// only the fields that carry a value are applied, so an omitted field is never wiped.
    /// </summary>
    private static ComicMetadataFieldFlags MetadataFields(BatchJobRequest request)
    {
        if (request.Fields is null)
        {
            return ComicMetadataFields.Supplied(request.Metadata!);
        }

        return ComicMetadataFields.TryParse(request.Fields, out var fields, out _) ? fields : ComicMetadataFieldFlags.None;
    }

    // RESTful endpoint: GET /api/jobs - List all jobs
    [HttpGet]
    public ActionResult<object> ListJobs()
    {
        var jobs = _jobRegistry.GetJobSummaries();
        var jobList = jobs.Select(j => new
        {
            job_id = j.JobId.ToString(),
            status = j.Status.ToString().ToLower(),
            total_items = j.TotalFiles,
            processed_items = j.ProcessedFiles,
            failed_items = j.FailedFiles,
            start_time = j.StartTime,
            end_time = j.EndTime
        }).ToList();

        return Ok(new { jobs = jobList, count = jobList.Count });
    }

    // RESTful endpoint: DELETE /api/jobs/{jobId} - Delete a job
    [HttpDelete("{jobId}")]
    public async Task<ActionResult> DeleteJob(Guid jobId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Delete requested for job {JobId}", jobId);
        var deleted = await _jobRegistry.DeleteJobAsync(jobId, cancellationToken);
        if (!deleted)
            return NotFound();

        return NoContent();
    }

    [HttpPost("{jobId}/cancel")]
    public ActionResult CancelJob(Guid jobId)
    {
        _logger.LogInformation("Cancel requested for job {JobId}", jobId);
        var cancelled = _jobRegistry.CancelJob(jobId);

        return cancelled
            ? Ok(new { success = true })
            : NotFound(new { success = false, error = "Job not found or already completed" });
    }

    /// <summary>
    /// Which files a batch operation can act on.
    /// </summary>
    [Flags]
    private enum BatchScope
    {
        None = 0,

        /// <summary>The files named in the request.</summary>
        Selected = 1,

        /// <summary>Every tracked file that is not a duplicate.</summary>
        All = 2,

        /// <summary>Every tracked file not yet fully processed that is not a duplicate.</summary>
        Unmarked = 4,

        Any = Selected | All | Unmarked
    }

    /// <summary>
    /// A row of <see cref="BatchOperations"/>.
    /// </summary>
    /// <param name="Scopes">The scopes the operation is offered for.</param>
    /// <param name="Start">Queues the job.</param>
    /// <param name="Validate">Checks operation-specific input; returns the problem, or null.</param>
    /// <param name="ActsOnEntry">
    /// The job affects the directory entry a path names rather than what it leads to, as
    /// deleting does, so named files are held to the stricter rule for entries.
    /// </param>
    private sealed record BatchOperation(
        BatchScope Scopes,
        Func<BatchInvocation, Task<Guid>> Start,
        Func<BatchJobRequest, string?>? Validate = null,
        bool ActsOnEntry = false);

    private sealed record BatchInvocation(
        IComicProcessorService Processor,
        IReadOnlyList<string> Files,
        BatchJobRequest Request,
        bool ForceReprocess);

    /// <summary>
    /// What a batch job is told to act on. The all and unmarked scopes need none of it.
    /// </summary>
    public class BatchJobRequest
    {
        /// <summary>The files to act on, for the selected scope.</summary>
        public List<string>? Files { get; set; } = new();

        /// <summary>Reprocess files already marked as done.</summary>
        public bool ForceReprocess { get; set; }

        /// <summary>The values to write, for update-metadata.</summary>
        public ComicMetadata? Metadata { get; set; }

        /// <summary>
        /// The fields to apply ("series", "title", "issue", "volume", "publisher", "year",
        /// "summary", "authors"/"writer", "tags"), for update-metadata. A listed field without
        /// a value in <see cref="Metadata"/> is removed; unlisted fields are kept. When omitted,
        /// every field that carries a value is applied and nothing is removed.
        /// </summary>
        public List<string>? Fields { get; set; }
    }

    /// <summary>
    /// The response to a request that started, or found nothing to do for, a batch job. It
    /// carries both snake_case (the web app) and camelCase (the mobile app) names.
    /// </summary>
    /// <param name="JobId">The job's id, or an empty GUID when nothing was queued.</param>
    /// <param name="TotalItems">How many files the job covers.</param>
    public sealed record BatchJobResponse(
        [property: JsonPropertyName("job_id")] string JobId,
        [property: JsonPropertyName("total_items")] int TotalItems)
    {
        [JsonPropertyName("jobId")]
        public string JobIdCamelCase => JobId;

        [JsonPropertyName("totalItems")]
        public int TotalItemsCamelCase => TotalItems;
    }
}
