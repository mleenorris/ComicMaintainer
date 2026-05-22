using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class ProcessController : ControllerBase
{
    private readonly IComicProcessorService _processor;
    private readonly IFileStoreService _fileStore;
    private readonly ILogger<ProcessController> _logger;

    public ProcessController(
        IComicProcessorService processor,
        IFileStoreService fileStore,
        ILogger<ProcessController> logger)
    {
        _processor = processor;
        _fileStore = fileStore;
        _logger = logger;
    }

    [HttpPost("process-all")]
    public async Task<ActionResult<object>> ProcessAll([FromQuery] bool stream = false, CancellationToken cancellationToken = default)
    {
        try
        {
            // Only queue files that have not yet been fully processed (respects database state).
            var allFiles = await _fileStore.GetAllFilesAsync(cancellationToken);
            var filePaths = allFiles
                .Where(f => !f.IsProcessed && !f.IsDuplicate)
                .Select(f => f.FilePath)
                .ToList();
            
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("Process all files requested, processing {Count} unprocessed files"), filePaths.Count);
            
            // Start processing job
            var jobId = await _processor.ProcessFilesAsync(filePaths, cancellationToken);
            
            return Ok(new { jobId, streaming = stream, totalFiles = filePaths.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error processing all files"));
            return StatusCode(500, "Error processing files");
        }
    }

    [HttpPost("process-selected")]
    public async Task<ActionResult<object>> ProcessSelected([FromBody] ProcessRequest request, [FromQuery] bool stream = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var jobId = await _processor.ProcessFilesAsync(request.Files, cancellationToken);
            return Ok(new { jobId, streaming = stream });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error processing selected files"));
            return StatusCode(500, "Error processing files");
        }
    }

    [HttpPost("rename-all")]
    public async Task<ActionResult<object>> RenameAll([FromQuery] bool stream = false, CancellationToken cancellationToken = default)
    {
        try
        {
            // Only queue files that have not yet been renamed (respects database state).
            var allFiles = await _fileStore.GetAllFilesAsync(cancellationToken);
            var filePaths = allFiles
                .Where(f => !f.IsRenamed && !f.IsDuplicate)
                .Select(f => f.FilePath)
                .ToList();
            
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("Rename all files requested, processing {Count} unrenamed files"), filePaths.Count);
            
            // Start rename job
            var jobId = await _processor.RenameFilesAsync(filePaths, cancellationToken);
            
            return Ok(new { jobId, streaming = stream, totalFiles = filePaths.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error renaming all files"));
            return StatusCode(500, "Error renaming files");
        }
    }

    [HttpPost("rename-selected")]
    public async Task<ActionResult<object>> RenameSelected([FromBody] ProcessRequest request, [FromQuery] bool stream = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request?.Files == null || request.Files.Count == 0)
            {
                return BadRequest("No files specified");
            }
            
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("Rename selected files requested, processing {Count} files"), request.Files.Count);
            
            // Start rename job
            var jobId = await _processor.RenameFilesAsync(request.Files, cancellationToken);
            
            return Ok(new { jobId, streaming = stream, totalFiles = request.Files.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error renaming selected files"));
            return StatusCode(500, "Error renaming files");
        }
    }

    [HttpPost("normalize-all")]
    public async Task<ActionResult<object>> NormalizeAll([FromQuery] bool stream = false, [FromQuery] bool forceReprocess = false, CancellationToken cancellationToken = default)
    {
        try
        {
            // When forceReprocess is true, include every tracked file (except
            // duplicates) so callers can re-run normalization against files
            // that are already DB-marked normalized — necessary after the
            // series metadata cache has changed (e.g. a manual match was
            // applied or a preferred-language setting changed) and the on-disk
            // <Series> needs to catch up. Without the flag we keep the
            // historical "skip already-normalized files" behaviour.
            var allFiles = await _fileStore.GetAllFilesAsync(cancellationToken);
            var filePaths = allFiles
                .Where(f => !f.IsDuplicate && (forceReprocess || !f.IsNormalized))
                .Select(f => f.FilePath)
                .ToList();
            
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("Normalize all files requested, processing {Count} files (forceReprocess={Force})"), filePaths.Count, forceReprocess);
            
            // Start normalize job
            var jobId = await _processor.NormalizeFilesAsync(filePaths, forceReprocess, cancellationToken);
            
            return Ok(new { jobId, streaming = stream, totalFiles = filePaths.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error normalizing all files"));
            return StatusCode(500, "Error normalizing files");
        }
    }

    [HttpPost("normalize-selected")]
    public async Task<ActionResult<object>> NormalizeSelected([FromBody] ProcessRequest request, [FromQuery] bool stream = false, [FromQuery] bool forceReprocess = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request?.Files == null || request.Files.Count == 0)
            {
                return BadRequest("No files specified");
            }
            
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("Normalize selected files requested, processing {Count} files (forceReprocess={Force})"), request.Files.Count, forceReprocess);
            
            // Start normalize job
            var jobId = await _processor.NormalizeFilesAsync(request.Files, forceReprocess, cancellationToken);
            
            return Ok(new { jobId, streaming = stream, totalFiles = request.Files.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error normalizing selected files"));
            return StatusCode(500, "Error normalizing files");
        }
    }

    public class ProcessRequest
    {
        public List<string> Files { get; set; } = new();
    }
}
