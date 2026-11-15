using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

public partial class FilesController
{
    [HttpPost("process")]
    [FailureMessage("Error processing file")]
    public async Task<ActionResult> ProcessFile([FromQuery] string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return BadRequest("File path is required");

        // Validate path is within watched directory to prevent path traversal attacks
        if (!IsPathSafe(filePath))
        {
            _logger.LogWarning("Attempt to process file outside watched directory: {FilePath}", LoggingHelper.SanitizeForLog(LoggingHelper.SanitizePathForLog(filePath)));
            return BadRequest("File path is outside the allowed directory");
        }

        var success = await _processor.ProcessFileAsync(filePath);
        if (!success)
            return BadRequest("Failed to process file");
            
        return Ok();
    }

    [HttpPost("process-batch")]
    [FailureMessage("Error starting batch processing")]
    public async Task<ActionResult<Guid>> ProcessBatch([FromBody] List<string> filePaths)
    {
        // Validate every path the batch job would process (it skips blank entries itself)
        foreach (var filePath in filePaths)
        {
            if (!string.IsNullOrWhiteSpace(filePath) && !IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to process file outside watched directory: {FilePath}", LoggingHelper.SanitizeForLog(LoggingHelper.SanitizePathForLog(filePath)));
                return BadRequest("One or more file paths are outside the allowed directory");
            }
        }

        var jobId = await _processor.ProcessFilesAsync(filePaths);
        return Ok(new { jobId });
    }

    // Note: Processed status is now computed from renamed && normalized states
    // No longer accepting manual updates to processed status

    [HttpPost("tags")]
    [FailureMessage("Error updating tags")]
    public async Task<ActionResult> UpdateTags([FromBody] UpdateTagsRequest request)
    {
        // A body without "files" (such as the old {"file", "tags"} format) binds to an empty
        // list; reject it instead of answering 200 having updated nothing
        if (request.Files is null || request.Files.Count == 0)
            return BadRequest("No files provided");

        // Validate every path before updating any file, so a rejected path fails the whole request
        foreach (var file in request.Files)
        {
            if (!string.IsNullOrWhiteSpace(file) && !IsPathSafe(file))
            {
                _logger.LogWarning("Attempt to update tags for file outside watched directory: {FilePath}", LoggingHelper.SanitizeForLog(LoggingHelper.SanitizePathForLog(file)));
                return BadRequest("One or more file paths are outside the allowed directory");
            }
        }

        var metadata = request.Metadata ?? new ComicMetadata();
        ComicMetadataFieldFlags fields;
        if (request.Fields is null)
        {
            fields = ComicMetadataFields.Supplied(metadata);
        }
        else if (!ComicMetadataFields.TryParse(request.Fields, out fields, out var unknownField))
        {
            return BadRequest(new { error = $"Unknown metadata field: {unknownField}" });
        }

        var files = request.Files.Where(file => !string.IsNullOrWhiteSpace(file)).ToList();
        if (files.Count > 0 && fields == ComicMetadataFieldFlags.None)
            return BadRequest(new { error = "No metadata fields specified" });

        var attempted = 0;
        var failed = new List<string>();
        foreach (var file in files)
        {
            attempted++;
            var result = await _processor.ApplyMetadataEditAsync(file, metadata, fields);
            if (!result.Succeeded)
                failed.Add(file);
        }

        // The processor answers false, having logged why, for a file it could not
        // write: missing, not a comic archive, or unreadable.
        if (failed.Count == 0)
            return Ok();
        if (failed.Count == attempted)
            return BadRequest(new { error = "Failed to update tags", failed });
        return StatusCode(StatusCodes.Status207MultiStatus, new { failed });
    }

    // RESTful endpoint: GET /api/files/scan-unmarked (read operation, no side effects)
    [HttpGet("scan-unmarked")]
    [FailureMessage("Error scanning files")]
    public async Task<ActionResult> ScanUnmarked(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(LoggingHelper.WithWebsitePrefix("ScanUnmarked: Scan unmarked files requested"));

        // The counts the file list and /api/files/counts use, with the same predicates as the
        // "processed" and "unprocessed" filters; no copy of the library is made to count it.
        var (totalCount, markedCount, unmarkedCount, _) = await _fileStore.GetFileCountsAsync(cancellationToken);

        _logger.LogInformation(LoggingHelper.WithWebsitePrefix("ScanUnmarked: File counts - Total: {TotalCount}, Unmarked: {UnmarkedCount}, Marked: {MarkedCount}"), 
            totalCount, unmarkedCount, markedCount);
            
        return Ok(new { 
            total_count = totalCount,
            unmarked_count = unmarkedCount,
            marked_count = markedCount
        });
    }

    // RESTful endpoint: POST /api/files/{encodedFilePath}/process
    [HttpPost("{encodedFilePath}/process")]
    [FailureMessage("Error processing file")]
    public async Task<ActionResult> ProcessFileByEncodedPath(string encodedFilePath, [FromQuery] bool forceReprocess = false)
    {
        var filePath = DecodeBase64UrlSafe(encodedFilePath);
        if (string.IsNullOrEmpty(filePath))
            return BadRequest("Invalid file path");

        if (!IsPathSafe(filePath))
        {
            _logger.LogWarning("Attempt to process file outside watched directory: {EncodedPath}", LoggingHelper.SanitizeForLog(encodedFilePath));
            return BadRequest("File path is outside the allowed directory");
        }

        var success = await _processor.ProcessFileAsync(filePath, forceReprocess);
        return success ? Ok() : BadRequest("Failed to process file");
    }

    public class UpdateTagsRequest
    {
        public List<string> Files { get; set; } = new();
        public ComicMetadata Metadata { get; set; } = new();

        /// <summary>
        /// The fields to apply; a listed field without a value in <see cref="Metadata"/> is
        /// removed and unlisted fields are kept. When omitted, every field that carries a value is
        /// applied and nothing is removed.
        /// </summary>
        public List<string>? Fields { get; set; }
    }

    public class ProcessedStatusRequest
    {
        public bool Processed { get; set; }
    }
}
