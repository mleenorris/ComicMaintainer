using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

public partial class FilesController
{
    // RESTful endpoint: POST /api/files/{encodedFilePath}/rename
    [HttpPost("{encodedFilePath}/rename")]
    [FailureMessage("Error renaming file")]
    public async Task<ActionResult> RenameFileByEncodedPath(string encodedFilePath, [FromQuery] bool forceReprocess = false, CancellationToken cancellationToken = default)
    {
        var filePath = DecodeBase64UrlSafe(encodedFilePath);
        if (string.IsNullOrEmpty(filePath))
            return BadRequest("Invalid file path");

        if (!IsPathSafe(filePath))
        {
            _logger.LogWarning("Attempt to rename file outside watched directory: {EncodedPath}", LoggingHelper.SanitizeForLog(encodedFilePath));
            return BadRequest("File path is outside the allowed directory");
        }

        var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
        _logger.LogInformation(LoggingHelper.WithWebsitePrefix("Rename requested for file: {FilePath}"), sanitizedPath);
            
        // Use the batch rename method with a single file
        var jobId = await _processor.RenameFilesAsync(new[] { filePath }, forceReprocess, cancellationToken);
            
        return Ok(new { message = "Rename job started", jobId });
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("~/api/rename-file")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [Obsolete("Use POST /api/files/{encodedFilePath}/rename instead. This alias will be removed in a future release.")]
    [FailureMessage("Error renaming file")]
    public async Task<ActionResult> RenameSingleFile([FromQuery] string filePath, [FromQuery] bool forceReprocess = false, CancellationToken cancellationToken = default)
    {
        if (!IsPathSafe(filePath))
        {
            _logger.LogWarning("Attempt to rename file outside watched directory: {FilePath}", LoggingHelper.SanitizeForLog(LoggingHelper.SanitizePathForLog(filePath)));
            return BadRequest("File path is outside the allowed directory");
        }

        var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
        _logger.LogInformation(LoggingHelper.WithWebsitePrefix("Rename requested for file: {FilePath}"), sanitizedPath);
            
        // Use the batch rename method with a single file
        var jobId = await _processor.RenameFilesAsync(new[] { filePath }, forceReprocess, cancellationToken);
            
        return Ok(new { jobId });
    }

    // RESTful endpoint: DELETE /api/files/{encodedFilePath}
    [HttpDelete("~/api/files/{encodedFilePath}")]
    [FailureMessage("Error deleting file")]
    public async Task<ActionResult> DeleteFileByEncodedPath(string encodedFilePath, CancellationToken cancellationToken = default)
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

            if (await DeleteSingleFileAsync(filePath, resolvedPath, cancellationToken))
            {
                // Log to processing history
                await LogHistoryAsync(filePath, "Delete", true);

                return Ok();
            }
            return NotFound();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelled while waiting for the file's lock, before anything was changed.
            return StatusCode(499, new { error = "Delete was cancelled" });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await RecordFailureAsync(DecodeBase64UrlSafe(encodedFilePath), "Delete", ex);
            throw;
        }
    }

    // Legacy endpoint for backward compatibility: DELETE /api/delete-file?filePath=...
    [HttpDelete("~/api/delete-file")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [Obsolete("Use DELETE /api/files/{encodedFilePath} instead. This alias will be removed in a future release.")]
    [FailureMessage("Error deleting file")]
    public async Task<ActionResult> DeleteFile([FromQuery] string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            // Validate path is within watched directory to prevent path traversal attacks
            if (!TryResolveSafePath(filePath, out var resolvedPath))
            {
                _logger.LogWarning("Attempt to delete file outside watched directory");
                return BadRequest("File path is outside the allowed directory");
            }

            if (await DeleteSingleFileAsync(filePath, resolvedPath, cancellationToken))
            {
                // Log to processing history
                await LogHistoryAsync(filePath, "Delete", true);

                return Ok();
            }
            return NotFound();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelled while waiting for the file's lock, before anything was changed.
            return StatusCode(499, new { error = "Delete was cancelled" });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await RecordFailureAsync(filePath, "Delete", ex);
            throw;
        }
    }

    /// <summary>
    /// Deletes one archive and its row while holding its file lock, so the delete never lands
    /// in the middle of another operation's rewrite or rename of the same archive (which would
    /// re-create it, or fail that operation). Returns false when there is no file to delete,
    /// including when another operation moved it while this one waited for the lock.
    /// </summary>
    private async Task<bool> DeleteSingleFileAsync(string filePath, string resolvedPath, CancellationToken cancellationToken)
    {
        using (await _fileOperationLock.AcquireAsync(resolvedPath, cancellationToken))
        {
            if (!System.IO.File.Exists(resolvedPath))
            {
                return false;
            }

            // Remove from file store first (unlikely to fail), then delete physical file
            // This order prevents orphaned file store entries if file deletion fails
            await _fileStore.RemoveFileAsync(filePath, CancellationToken.None);
            System.IO.File.Delete(resolvedPath);
            _notifications.LibraryChanged();
            return true;
        }
    }

    /// <summary>
    /// Remove stale database entries for files that no longer exist on disk. Refuses (409)
    /// while the library looks unmounted, and asks for confirmation (409 with
    /// <c>requiresConfirmation</c>; repeat with <c>?confirm=true</c>) before removing more
    /// than the configured share of the library; see <see cref="StaleCleanupResponses"/>.
    /// </summary>
    [HttpPost("cleanup-stale")]
    public async Task<ActionResult> CleanupStaleEntries([FromQuery] bool confirm = false, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Cleanup stale entries endpoint called (confirmed: {Confirmed})", confirm);
        var result = await _fileStore.CleanupStaleEntriesAsync(new StaleCleanupOptions(IsManual: true, Confirmed: confirm), cancellationToken);
        return StaleCleanupResponses.ToActionResult(this, result);
    }
}
