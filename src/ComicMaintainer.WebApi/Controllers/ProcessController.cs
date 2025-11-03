using ComicMaintainer.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api")]
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
            // Get all files from the file store
            var allFiles = await _fileStore.GetAllFilesAsync(cancellationToken);
            var filePaths = allFiles.Select(f => f.FilePath).ToList();
            
            _logger.LogInformation("Process all files requested, processing {Count} files", filePaths.Count);
            
            // Start processing job
            var jobId = await _processor.ProcessFilesAsync(filePaths, cancellationToken);
            
            return Ok(new { jobId, streaming = stream, totalFiles = filePaths.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing all files");
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
            _logger.LogError(ex, "Error processing selected files");
            return StatusCode(500, "Error processing files");
        }
    }

    [HttpPost("rename-all")]
    public async Task<ActionResult<object>> RenameAll([FromQuery] bool stream = false, CancellationToken cancellationToken = default)
    {
        try
        {
            // Get all files from the file store
            var allFiles = await _fileStore.GetAllFilesAsync(cancellationToken);
            var filePaths = allFiles.Select(f => f.FilePath).ToList();
            
            _logger.LogInformation("Rename all files requested, processing {Count} files", filePaths.Count);
            
            // Start rename job
            var jobId = await _processor.RenameFilesAsync(filePaths, cancellationToken);
            
            return Ok(new { jobId, streaming = stream, totalFiles = filePaths.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renaming all files");
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
            
            _logger.LogInformation("Rename selected files requested, processing {Count} files", request.Files.Count);
            
            // Start rename job
            var jobId = await _processor.RenameFilesAsync(request.Files, cancellationToken);
            
            return Ok(new { jobId, streaming = stream, totalFiles = request.Files.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renaming selected files");
            return StatusCode(500, "Error renaming files");
        }
    }

    [HttpPost("normalize-all")]
    public async Task<ActionResult<object>> NormalizeAll([FromQuery] bool stream = false, CancellationToken cancellationToken = default)
    {
        try
        {
            // Get all files from the file store
            var allFiles = await _fileStore.GetAllFilesAsync(cancellationToken);
            var filePaths = allFiles.Select(f => f.FilePath).ToList();
            
            _logger.LogInformation("Normalize all files requested, processing {Count} files", filePaths.Count);
            
            // Start normalize job
            var jobId = await _processor.NormalizeFilesAsync(filePaths, cancellationToken);
            
            return Ok(new { jobId, streaming = stream, totalFiles = filePaths.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error normalizing all files");
            return StatusCode(500, "Error normalizing files");
        }
    }

    [HttpPost("normalize-selected")]
    public async Task<ActionResult<object>> NormalizeSelected([FromBody] ProcessRequest request, [FromQuery] bool stream = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request?.Files == null || request.Files.Count == 0)
            {
                return BadRequest("No files specified");
            }
            
            _logger.LogInformation("Normalize selected files requested, processing {Count} files", request.Files.Count);
            
            // Start normalize job
            var jobId = await _processor.NormalizeFilesAsync(request.Files, cancellationToken);
            
            return Ok(new { jobId, streaming = stream, totalFiles = request.Files.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error normalizing selected files");
            return StatusCode(500, "Error normalizing files");
        }
    }

    public class ProcessRequest
    {
        public List<string> Files { get; set; } = new();
    }
}
