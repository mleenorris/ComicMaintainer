using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class JobsController : ControllerBase
{
    private readonly IComicProcessorService _processor;
    private readonly IFileStoreService _fileStore;
    private readonly ILogger<JobsController> _logger;

    public JobsController(
        IComicProcessorService processor,
        IFileStoreService fileStore,
        ILogger<JobsController> logger)
    {
        _processor = processor;
        _fileStore = fileStore;
        _logger = logger;
    }

    [HttpGet("{jobId}")]
    public ActionResult<object> GetJob(Guid jobId)
    {
        try
        {
            var job = _processor.GetJob(jobId);
            if (job == null)
                return NotFound();
            
            // Return in snake_case format expected by frontend
            // Build a set of successfully processed files (files that were processed but not in error list)
            var processedFilesList = new List<string>();
            var fileIndex = 0;
            foreach (var file in job.Files)
            {
                if (fileIndex < job.ProcessedFiles + job.FailedFiles)
                {
                    processedFilesList.Add(file);
                }
                fileIndex++;
            }
            
            return Ok(new
            {
                job_id = job.JobId.ToString(),
                status = job.Status.ToString().ToLower(),
                total_items = job.TotalFiles,
                processed_items = job.ProcessedFiles,
                failed_items = job.FailedFiles,
                current_file = job.CurrentFile,
                start_time = job.StartTime,
                end_time = job.EndTime,
                results = job.Files.Select(f => new
                {
                    file = f,
                    success = processedFilesList.Contains(f) && !job.Errors.ContainsKey(f),
                    error = job.Errors.ContainsKey(f) ? job.Errors[f] : null
                }).ToList()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting job {JobId}", jobId);
            return StatusCode(500, "Error retrieving job");
        }
    }

    [HttpGet("~/api/active-job")]
    public ActionResult<object> GetActiveJob()
    {
        try
        {
            var job = _processor.GetActiveJob();
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active job");
            return StatusCode(500, "Error retrieving active job");
        }
    }

    private string DetermineJobTitle(ProcessingJob job)
    {
        // Determine a user-friendly title based on job characteristics
        return job.Status switch
        {
            JobStatus.Queued when job.ProcessedFiles == 0 => "Processing Files...",
            JobStatus.Running => $"Processing {job.TotalFiles} files...",
            JobStatus.Completed => $"Completed {job.TotalFiles} files",
            JobStatus.Failed => "Processing Failed",
            JobStatus.Cancelled => "Processing Cancelled",
            _ => "Processing..."
        };
    }

    [HttpPost("process-all")]
    public async Task<ActionResult<object>> ProcessAll()
    {
        try
        {
            _logger.LogDebug(LoggingHelper.WithWebsitePrefix("ProcessAll: Starting process all files request"));
            
            // Get all files from the file store
            var allFiles = await _fileStore.GetAllFilesAsync();
            var filePaths = allFiles.Select(f => f.FilePath).ToList();
            
            _logger.LogDebug(LoggingHelper.WithWebsitePrefix("ProcessAll: Retrieved {TotalFiles} files from file store"), filePaths.Count);
            
            if (filePaths.Count == 0)
            {
                _logger.LogInformation(LoggingHelper.WithWebsitePrefix("ProcessAll: No files found to process"));
                return Ok(new { job_id = Guid.Empty.ToString(), total_items = 0 });
            }
            
            // Start the process job
            var jobId = await _processor.ProcessFilesAsync(filePaths);
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("ProcessAll: Process all files requested, job ID: {JobId}, total files: {TotalFiles}"), jobId, filePaths.Count);
            _logger.LogDebug(LoggingHelper.WithWebsitePrefix("ProcessAll: Job created successfully with ID: {JobId}"), jobId);
            
            return Ok(new { job_id = jobId.ToString(), total_items = filePaths.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("ProcessAll: Error starting process all job"));
            return StatusCode(500, new { error = "Error starting job" });
        }
    }

    [HttpPost("rename-all")]
    public async Task<ActionResult<object>> RenameAll()
    {
        try
        {
            _logger.LogDebug(LoggingHelper.WithWebsitePrefix("RenameAll: Starting rename all files request"));
            
            // Get all files from the file store
            var allFiles = await _fileStore.GetAllFilesAsync();
            var filePaths = allFiles.Select(f => f.FilePath).ToList();
            
            _logger.LogDebug(LoggingHelper.WithWebsitePrefix("RenameAll: Retrieved {TotalFiles} files from file store"), filePaths.Count);
            
            if (filePaths.Count == 0)
            {
                _logger.LogInformation(LoggingHelper.WithWebsitePrefix("RenameAll: No files found to rename"));
                return Ok(new { job_id = Guid.Empty.ToString(), total_items = 0 });
            }
            
            // Start the rename job
            var jobId = await _processor.RenameFilesAsync(filePaths);
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("RenameAll: Rename all files requested, job ID: {JobId}, total files: {TotalFiles}"), jobId, filePaths.Count);
            _logger.LogDebug(LoggingHelper.WithWebsitePrefix("RenameAll: Job created successfully with ID: {JobId}"), jobId);
            
            return Ok(new { job_id = jobId.ToString(), total_items = filePaths.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("RenameAll: Error starting rename all job"));
            return StatusCode(500, new { error = "Error starting job" });
        }
    }

    [HttpPost("normalize-all")]
    public async Task<ActionResult<object>> NormalizeAll()
    {
        try
        {
            _logger.LogDebug(LoggingHelper.WithWebsitePrefix("NormalizeAll: Starting normalize all files request"));
            
            // Get all files from the file store
            var allFiles = await _fileStore.GetAllFilesAsync();
            var filePaths = allFiles.Select(f => f.FilePath).ToList();
            
            _logger.LogDebug(LoggingHelper.WithWebsitePrefix("NormalizeAll: Retrieved {TotalFiles} files from file store"), filePaths.Count);
            
            if (filePaths.Count == 0)
            {
                _logger.LogInformation(LoggingHelper.WithWebsitePrefix("NormalizeAll: No files found to normalize"));
                return Ok(new { job_id = Guid.Empty.ToString(), total_items = 0 });
            }
            
            // Start the normalize job
            var jobId = await _processor.NormalizeFilesAsync(filePaths);
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("NormalizeAll: Normalize all files requested, job ID: {JobId}, total files: {TotalFiles}"), jobId, filePaths.Count);
            _logger.LogDebug(LoggingHelper.WithWebsitePrefix("NormalizeAll: Job created successfully with ID: {JobId}"), jobId);
            
            return Ok(new { job_id = jobId.ToString(), total_items = filePaths.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("NormalizeAll: Error starting normalize all job"));
            return StatusCode(500, new { error = "Error starting job" });
        }
    }

    [HttpPost("process-selected")]
    public async Task<ActionResult<object>> ProcessSelected([FromBody] ProcessSelectedRequest request)
    {
        try
        {
            _logger.LogDebug(LoggingHelper.WithWebsitePrefix("ProcessSelected: Starting process selected files request"));
            
            if (request.Files == null || request.Files.Count == 0)
            {
                _logger.LogDebug(LoggingHelper.WithWebsitePrefix("ProcessSelected: No files specified in request"));
                return BadRequest(new { error = "No files specified" });
            }
            
            _logger.LogDebug(LoggingHelper.WithWebsitePrefix("ProcessSelected: Processing {SelectedCount} selected files"), request.Files.Count);
            
            var jobId = await _processor.ProcessFilesAsync(request.Files);
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("ProcessSelected: Process selected files requested, job ID: {JobId}, total files: {TotalFiles}"), jobId, request.Files.Count);
            _logger.LogDebug(LoggingHelper.WithWebsitePrefix("ProcessSelected: Job created successfully with ID: {JobId}"), jobId);
            
            return Ok(new { job_id = jobId.ToString(), total_items = request.Files.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("ProcessSelected: Error starting process selected job"));
            return StatusCode(500, new { error = "Error starting job" });
        }
    }

    [HttpPost("process-unmarked")]
    public async Task<ActionResult<object>> ProcessUnmarked()
    {
        try
        {
            return await ProcessUnprocessedFilesAsync("Process unmarked files");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting process unmarked job");
            return StatusCode(500, new { error = "Error starting job" });
        }
    }

    [HttpPost("rename-unmarked")]
    public async Task<ActionResult<object>> RenameUnmarked()
    {
        try
        {
            _logger.LogDebug("RenameUnmarked: Starting rename unmarked files request");
            
            // Get all unprocessed files
            var files = await _fileStore.GetFilteredFilesAsync("unprocessed");
            var filePaths = files.Select(f => f.FilePath).ToList();
            
            _logger.LogDebug("RenameUnmarked: Retrieved {UnprocessedCount} unprocessed files after filtering", filePaths.Count);
            
            if (filePaths.Count == 0)
            {
                _logger.LogInformation("RenameUnmarked: No unmarked files found to rename");
                return Ok(new { job_id = Guid.Empty.ToString(), total_items = 0 });
            }
            
            // Start the rename job
            var jobId = await _processor.RenameFilesAsync(filePaths);
            _logger.LogInformation("RenameUnmarked: Rename unmarked files requested, job ID: {JobId}, total files: {TotalFiles}", jobId, filePaths.Count);
            _logger.LogDebug("RenameUnmarked: Job created successfully with ID: {JobId}", jobId);
            
            return Ok(new { job_id = jobId.ToString(), total_items = filePaths.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RenameUnmarked: Error starting rename unmarked job");
            return StatusCode(500, new { error = "Error starting job" });
        }
    }

    [HttpPost("normalize-unmarked")]
    public async Task<ActionResult<object>> NormalizeUnmarked()
    {
        try
        {
            _logger.LogDebug("NormalizeUnmarked: Starting normalize unmarked files request");
            
            // Get all unprocessed files
            var files = await _fileStore.GetFilteredFilesAsync("unprocessed");
            var filePaths = files.Select(f => f.FilePath).ToList();
            
            _logger.LogDebug("NormalizeUnmarked: Retrieved {UnprocessedCount} unprocessed files after filtering", filePaths.Count);
            
            if (filePaths.Count == 0)
            {
                _logger.LogInformation("NormalizeUnmarked: No unmarked files found to normalize");
                return Ok(new { job_id = Guid.Empty.ToString(), total_items = 0 });
            }
            
            // Start the normalize job
            var jobId = await _processor.NormalizeFilesAsync(filePaths);
            _logger.LogInformation("NormalizeUnmarked: Normalize unmarked files requested, job ID: {JobId}, total files: {TotalFiles}", jobId, filePaths.Count);
            _logger.LogDebug("NormalizeUnmarked: Job created successfully with ID: {JobId}", jobId);
            
            return Ok(new { job_id = jobId.ToString(), total_items = filePaths.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NormalizeUnmarked: Error starting normalize unmarked job");
            return StatusCode(500, new { error = "Error starting job" });
        }
    }

    [HttpPost("rename-selected")]
    public async Task<ActionResult<object>> RenameSelected([FromBody] ProcessSelectedRequest request)
    {
        try
        {
            if (request.Files == null || request.Files.Count == 0)
            {
                return BadRequest(new { error = "No files specified" });
            }
            
            var jobId = await _processor.RenameFilesAsync(request.Files);
            _logger.LogInformation("Rename selected files requested, job ID: {JobId}, total files: {TotalFiles}", jobId, request.Files.Count);
            
            return Ok(new { job_id = jobId.ToString(), total_items = request.Files.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting rename selected job");
            return StatusCode(500, new { error = "Error starting job" });
        }
    }

    [HttpPost("normalize-selected")]
    public async Task<ActionResult<object>> NormalizeSelected([FromBody] ProcessSelectedRequest request)
    {
        try
        {
            if (request.Files == null || request.Files.Count == 0)
            {
                return BadRequest(new { error = "No files specified" });
            }
            
            var jobId = await _processor.NormalizeFilesAsync(request.Files);
            _logger.LogInformation("Normalize selected files requested, job ID: {JobId}, total files: {TotalFiles}", jobId, request.Files.Count);
            
            return Ok(new { job_id = jobId.ToString(), total_items = request.Files.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting normalize selected job");
            return StatusCode(500, new { error = "Error starting job" });
        }
    }

    [HttpPost("update-metadata-selected")]
    public async Task<ActionResult<object>> UpdateMetadataSelected([FromBody] UpdateMetadataSelectedRequest request)
    {
        try
        {
            if (request.Files == null || request.Files.Count == 0)
            {
                return BadRequest(new { error = "No files specified" });
            }

            if (request.Metadata == null)
            {
                return BadRequest(new { error = "No metadata specified" });
            }
            
            var jobId = await _processor.UpdateMetadataAsync(request.Files, request.Metadata);
            _logger.LogInformation("Update metadata for selected files requested, job ID: {JobId}, total files: {TotalFiles}", jobId, request.Files.Count);
            
            return Ok(new { job_id = jobId.ToString(), total_items = request.Files.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting update metadata selected job");
            return StatusCode(500, new { error = "Error starting job" });
        }
    }

    // RESTful endpoint: GET /api/jobs - List all jobs
    [HttpGet]
    public ActionResult<object> ListJobs()
    {
        try
        {
            var jobs = _processor.GetAllJobs();
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing jobs");
            return StatusCode(500, "Error retrieving jobs");
        }
    }

    // RESTful endpoint: DELETE /api/jobs/{jobId} - Delete a job
    [HttpDelete("{jobId}")]
    public ActionResult DeleteJob(Guid jobId)
    {
        try
        {
            _logger.LogInformation("Delete requested for job {JobId}", jobId);
            var deleted = _processor.DeleteJob(jobId);
            if (!deleted)
                return NotFound();
            
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting job {JobId}", jobId);
            return StatusCode(500, "Error deleting job");
        }
    }

    [HttpPost("{jobId}/cancel")]
    public ActionResult CancelJob(Guid jobId)
    {
        try
        {
            _logger.LogInformation("Cancel requested for job {JobId}", jobId);
            var cancelled = _processor.CancelJob(jobId);
            
            if (cancelled)
            {
                return Ok(new { success = true });
            }
            else
            {
                return NotFound(new { success = false, error = "Job not found or already completed" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling job {JobId}", jobId);
            return StatusCode(500, new { success = false, error = "Error cancelling job" });
        }
    }

    private async Task<ActionResult<object>> ProcessUnprocessedFilesAsync(string logMessage)
    {
        _logger.LogDebug("ProcessUnprocessedFilesAsync: Starting request - {LogMessage}", logMessage);
        
        // Get all unprocessed files
        var files = await _fileStore.GetFilteredFilesAsync("unprocessed");
        var filePaths = files.Select(f => f.FilePath).ToList();
        
        _logger.LogDebug("ProcessUnprocessedFilesAsync: Retrieved {UnprocessedCount} unprocessed files after filtering", filePaths.Count);
        
        if (filePaths.Count == 0)
        {
            _logger.LogInformation("ProcessUnprocessedFilesAsync: {LogMessage} - No unprocessed files found", logMessage);
            return Ok(new { job_id = Guid.Empty.ToString(), total_items = 0 });
        }
        
        // Start the processing job
        var jobId = await _processor.ProcessFilesAsync(filePaths);
        _logger.LogInformation("ProcessUnprocessedFilesAsync: {LogMessage} requested, job ID: {JobId}, total files: {TotalFiles}", logMessage, jobId, filePaths.Count);
        _logger.LogDebug("ProcessUnprocessedFilesAsync: Job created successfully with ID: {JobId}", jobId);
        
        return Ok(new { job_id = jobId.ToString(), total_items = filePaths.Count });
    }

    public class ProcessSelectedRequest
    {
        public List<string> Files { get; set; } = new();
    }

    public class UpdateMetadataSelectedRequest
    {
        public List<string> Files { get; set; } = new();
        public ComicMetadata Metadata { get; set; } = new();
    }
}
