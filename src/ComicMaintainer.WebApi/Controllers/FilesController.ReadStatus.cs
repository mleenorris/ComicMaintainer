using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

public partial class FilesController
{
    /// <summary>
    /// Mark a single file as read or unread
    /// </summary>
    [HttpPost("~/api/files/{encodedFilePath}/read")]
    [PerUserWriteOperation]
    public async Task<ActionResult> MarkFileRead(string encodedFilePath, [FromBody] ReadStatusRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to mark file read outside watched directory: {EncodedPath}", LoggingHelper.SanitizeForLog(encodedFilePath));
                return BadRequest("File path is outside the allowed directory");
            }

            await _fileStore.MarkFileReadAsync(filePath, request.Read, cancellationToken);
            return Ok();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499, new { error = "Read status update was cancelled" });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var sanitizedEncodedPath = LoggingHelper.SanitizeForLog(encodedFilePath);
            _logger.LogError(ex, "Error marking file read status for encoded path {EncodedPath}", sanitizedEncodedPath);
            return StatusCode(500, "Error updating read status");
        }
    }

    /// <summary>
    /// Mark multiple files as read or unread
    /// </summary>
    [HttpPost("~/api/files/read-batch")]
    [PerUserWriteOperation]
    public async Task<ActionResult> MarkFilesReadBatch([FromBody] ReadBatchRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request.Files == null || !request.Files.Any())
                return BadRequest("No files provided");

            // Validate all paths are safe
            foreach (var filePath in request.Files)
            {
                if (!IsPathSafe(filePath))
                {
                    _logger.LogWarning("Attempt to mark read status for file outside watched directory: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                    return BadRequest("One or more file paths are outside the allowed directory");
                }
            }

            await _fileStore.MarkFilesReadAsync(request.Files, request.Read, cancellationToken);
            return Ok();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499, new { error = "Read status update was cancelled" });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error marking files read status");
            return StatusCode(500, "Error updating read status");
        }
    }

    /// <summary>
    /// Mark every file in a scope read or unread for the current user: the whole library
    /// (<c>scope = "library"</c>), one series (<c>"series"</c>, <c>id</c> = series id) or one
    /// folder (<c>"folder"</c>, <c>id</c> = folder key). The scope is resolved against the
    /// database and applied in one set-based transaction, so the client never has to download
    /// and post back the whole file list. Responds 499 when the request is cancelled; nothing
    /// is changed then.
    /// </summary>
    [HttpPost("~/api/files/read-all")]
    [PerUserWriteOperation]
    public async Task<ActionResult> MarkScopeRead([FromBody] ReadScopeRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            FileReadScope scope;
            switch ((request.Scope ?? "library").Trim().ToLowerInvariant())
            {
                case "library":
                    scope = FileReadScope.Library;
                    break;
                case "folder":
                    scope = FileReadScope.Folder(request.Id ?? string.Empty);
                    break;
                case "series":
                    if (string.IsNullOrWhiteSpace(request.Id))
                        return BadRequest("A series id is required");
                    var series = await _seriesLibrary.GetSeriesIssuesAsync(request.Id, perPage: -1, cancellationToken: cancellationToken);
                    if (series is null)
                        return NotFound("Series not found");
                    scope = FileReadScope.Files(series.Issues.Select(issue => issue.FilePath));
                    break;
                default:
                    return BadRequest("Scope must be 'library', 'series' or 'folder'");
            }

            var updated = await _fileStore.MarkScopeReadAsync(scope, request.Read, cancellationToken);
            return Ok(new { success = true, updated });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499, new { error = "Read status update was cancelled" });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error marking {Scope} read status", LoggingHelper.SanitizeForLog(request.Scope));
            return StatusCode(500, "Error updating read status");
        }
    }

    public class ReadStatusRequest
    {
        public bool Read { get; set; }
    }

    public class ReadScopeRequest
    {
        public bool Read { get; set; }

        /// <summary><c>library</c> (default), <c>series</c> or <c>folder</c>.</summary>
        public string? Scope { get; set; }

        /// <summary>The series id or folder key; unused for <c>library</c>.</summary>
        public string? Id { get; set; }
    }

    public class ReadBatchRequest
    {
        public List<string> Files { get; set; } = new();
        public bool Read { get; set; }
    }
}
