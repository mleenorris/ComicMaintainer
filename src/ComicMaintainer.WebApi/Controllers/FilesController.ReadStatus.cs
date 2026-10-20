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
                return BadRequest("Invalid file path");

            await _fileStore.MarkFileReadAsync(filePath, request.Read, cancellationToken);
            return Ok();
        }
        catch (Exception ex)
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking files read status");
            return StatusCode(500, "Error updating read status");
        }
    }

    public class ReadStatusRequest
    {
        public bool Read { get; set; }
    }

    public class ReadBatchRequest
    {
        public List<string> Files { get; set; } = new();
        public bool Read { get; set; }
    }
}
