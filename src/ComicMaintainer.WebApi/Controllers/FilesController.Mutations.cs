using ComicMaintainer.Core.Utilities;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

public partial class FilesController
{
    // RESTful endpoint: POST /api/files/{encodedFilePath}/rename
    [HttpPost("{encodedFilePath}/rename")]
    public async Task<ActionResult> RenameFileByEncodedPath(string encodedFilePath, [FromQuery] bool forceReprocess = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            if (!IsPathSafe(filePath))
                return BadRequest("Invalid file path");

            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("Rename requested for file: {FilePath}"), sanitizedPath);
            
            // Use the batch rename method with a single file
            var jobId = await _processor.RenameFilesAsync(new[] { filePath }, forceReprocess, cancellationToken);
            
            return Ok(new { message = "Rename job started", jobId });
        }
        catch (Exception ex)
        {
            var sanitizedEncodedPath = LoggingHelper.SanitizeForLog(encodedFilePath);
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error renaming file with encoded path {EncodedPath}"), sanitizedEncodedPath);
            return StatusCode(500, "Error renaming file");
        }
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("~/api/rename-file")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [Obsolete("Use POST /api/files/{encodedFilePath}/rename instead. This alias is scheduled for removal in v3.0.")]
    public async Task<ActionResult> RenameSingleFile([FromQuery] string filePath, [FromQuery] bool forceReprocess = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsPathSafe(filePath))
                return BadRequest("Invalid file path");

            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("Rename requested for file: {FilePath}"), sanitizedPath);
            
            // Use the batch rename method with a single file
            var jobId = await _processor.RenameFilesAsync(new[] { filePath }, forceReprocess, cancellationToken);
            
            return Ok(new { jobId });
        }
        catch (Exception ex)
        {
            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error renaming file {FilePath}"), sanitizedPath);
            return StatusCode(500, "Error renaming file");
        }
    }

    // RESTful endpoint: DELETE /api/files/{encodedFilePath}
    [HttpDelete("~/api/files/{encodedFilePath}")]
    public async Task<ActionResult> DeleteFileByEncodedPath(string encodedFilePath)
    {
        try
        {
            // Decode the base64 URL-safe encoded file path
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            // Validate path is within watched directory to prevent path traversal attacks
            if (!TryResolveSafePath(filePath, out var resolvedPath))
            {
                _logger.LogWarning("Attempt to delete file outside watched directory: {EncodedPath}", LoggingHelper.SanitizeForLog(encodedFilePath));
                return BadRequest("File path is outside the allowed directory");
            }

            if (System.IO.File.Exists(resolvedPath))
            {
                // Remove from file store first (unlikely to fail), then delete physical file
                // This order prevents orphaned file store entries if file deletion fails
                await _fileStore.RemoveFileAsync(filePath);
                System.IO.File.Delete(resolvedPath);
                
                // Log to processing history
                await LogHistoryAsync(filePath, "Delete", true);
                
                return Ok();
            }
            return NotFound();
        }
        catch (Exception ex)
        {
            var sanitizedEncodedPath = LoggingHelper.SanitizeForLog(encodedFilePath);
            _logger.LogError(ex, "Error deleting file with encoded path {EncodedPath}", sanitizedEncodedPath);
            
            // Log to processing history
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            await LogHistoryAsync(filePath, "Delete", false, ex.Message);
            
            return StatusCode(500, "Error deleting file");
        }
    }

    // Legacy endpoint for backward compatibility: DELETE /api/delete-file?filePath=...
    [HttpDelete("~/api/delete-file")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [Obsolete("Use DELETE /api/files/{encodedFilePath} instead. This alias is scheduled for removal in v3.0.")]
    public async Task<ActionResult> DeleteFile([FromQuery] string filePath)
    {
        try
        {
            // Validate path is within watched directory to prevent path traversal attacks
            if (!TryResolveSafePath(filePath, out var resolvedPath))
            {
                _logger.LogWarning("Attempt to delete file outside watched directory");
                return BadRequest("File path is outside the allowed directory");
            }

            if (System.IO.File.Exists(resolvedPath))
            {
                // Remove from file store first (unlikely to fail), then delete physical file
                // This order prevents orphaned file store entries if file deletion fails
                await _fileStore.RemoveFileAsync(filePath);
                System.IO.File.Delete(resolvedPath);
                
                // Log to processing history
                await LogHistoryAsync(filePath, "Delete", true);
                
                return Ok();
            }
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting file");
            
            // Log to processing history
            await LogHistoryAsync(filePath, "Delete", false, ex.Message);
            
            return StatusCode(500, "Error deleting file");
        }
    }

    /// <summary>
    /// Remove stale database entries for files that no longer exist on disk
    /// </summary>
    [HttpPost("cleanup-stale")]
    public async Task<ActionResult<object>> CleanupStaleEntries(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Cleanup stale entries endpoint called");
            var removedCount = await _fileStore.CleanupStaleEntriesAsync(cancellationToken);
            
            return Ok(new 
            { 
                success = true, 
                removedCount = removedCount,
                message = $"Removed {removedCount} stale database entries"
            });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, new { error = "Cleanup operation was cancelled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during stale entry cleanup");
            return StatusCode(500, new { error = "Failed to cleanup stale entries" });
        }
    }
}
