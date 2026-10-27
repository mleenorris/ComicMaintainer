using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

public partial class FilesController
{
    [HttpGet("metadata")]
    public async Task<ActionResult<ComicMetadata>> GetMetadata([FromQuery] string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("File path is required");

            // Validate path is within watched directory to prevent path traversal attacks
            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to get metadata for file outside watched directory: {FilePath}", LoggingHelper.SanitizeForLog(LoggingHelper.SanitizePathForLog(filePath)));
                return BadRequest("File path is outside the allowed directory");
            }

            var metadata = await _processor.GetMetadataAsync(filePath);
            if (metadata == null)
                return NotFound();
            
            return Ok(metadata);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting metadata for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return StatusCode(500, "Error retrieving metadata");
        }
    }

    [HttpGet("series-resolution")]
    public async Task<ActionResult<object>> GetSeriesResolution([FromQuery] string filePath, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("File path is required");

            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to resolve series for file outside watched directory: {FilePath}",
                    LoggingHelper.SanitizeForLog(LoggingHelper.SanitizePathForLog(filePath)));
                return BadRequest("File path is outside the allowed directory");
            }

            if (_seriesNameResolver is null)
            {
                return StatusCode(503, "Series name resolver not available");
            }

            // Read the file's current metadata so the resolver can also
            // evaluate the file's existing <Series> tag as a candidate.
            // Missing/unreadable metadata is fine — the resolver falls back
            // to the folder name in that case.
            var metadata = await _processor.GetMetadataAsync(filePath, cancellationToken);

            // mutateCache:false — this endpoint is a read-only preview and
            // must not side-effect the alias index just by being polled.
            var resolution = await _seriesNameResolver.ResolveAsync(
                filePath,
                metadata,
                mutateCache: false,
                cancellationToken);

            return Ok(new
            {
                filePath,
                actualSeries = metadata?.Series,
                resolvedSeries = resolution.ResolvedSeries,
                winningStep = resolution.WinningStep.ToString(),
                candidates = resolution.Candidates,
                folderSeries = resolution.FolderSeries,
                matchedCacheKey = resolution.MatchedCacheKey,
                appliedLanguage = resolution.AppliedLanguage,
                explanation = resolution.Explanation,
                matchesActual = !string.IsNullOrWhiteSpace(metadata?.Series)
                    && string.Equals(metadata!.Series, resolution.ResolvedSeries, StringComparison.OrdinalIgnoreCase)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resolving series for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return StatusCode(500, "Error resolving series");
        }
    }

    [HttpPut("metadata")]
    public async Task<ActionResult> UpdateMetadata([FromQuery] string filePath, [FromBody] ComicMetadata metadata)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("File path is required");

            // Validate path is within watched directory to prevent path traversal attacks
            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to update metadata for file outside watched directory: {FilePath}", LoggingHelper.SanitizeForLog(LoggingHelper.SanitizePathForLog(filePath)));
                return BadRequest("File path is outside the allowed directory");
            }

            // Capture before state
            var beforeMetadata = await _processor.GetMetadataAsync(filePath);
            
            var success = await _processor.UpdateMetadataAsync(filePath, metadata);
            if (!success)
            {
                await LogHistoryAsync(filePath, "Update Metadata", false, "Failed to update metadata");
                if (_eventBroadcaster is not null)
                {
                    try { await _eventBroadcaster.BroadcastFileProcessedAsync(Path.GetFileName(filePath), false, "Failed to update metadata"); }
                    catch (Exception bex) { _logger.LogDebug(bex, "Failed to broadcast file_processed for metadata update failure"); }
                }
                return BadRequest("Failed to update metadata");
            }
            
            // Log with before/after metadata
            var filename = Path.GetFileName(filePath);
            await LogHistoryWithChangesAsync(filePath, "Update Metadata", true, null,
                filename, filename, beforeMetadata, metadata);

            // Broadcast a file_processed event so the UI patches the affected
            // folder/series detail in place instead of waiting for a full
            // file_list_updated debounce.
            if (_eventBroadcaster is not null)
            {
                try { await _eventBroadcaster.BroadcastFileProcessedAsync(filename, true); }
                catch (Exception bex) { _logger.LogDebug(bex, "Failed to broadcast file_processed for metadata update"); }
            }
            
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating metadata for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            await LogHistoryAsync(filePath, "Update Metadata", false, ex.Message);
            return StatusCode(500, "Error updating metadata");
        }
    }


    [HttpPatch("metadata")]
    public async Task<ActionResult> PatchMetadata([FromQuery] string filePath, [FromBody] PatchMetadataRequest body, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("File path is required");

            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to patch metadata for file outside watched directory: {FilePath}", LoggingHelper.SanitizeForLog(LoggingHelper.SanitizePathForLog(filePath)));
                return BadRequest("File path is outside the allowed directory");
            }

            if (body?.Metadata is null)
                return BadRequest("Metadata patch is required");

            var patch = body.Metadata;
            var lockFields = DetermineEditedFields(patch);
            if (body.LockFields is not null)
            {
                foreach (var field in body.LockFields)
                {
                    lockFields |= ParseMetadataField(field);
                }
            }

            await _fileStore.ApplyUserMetadataEditAsync(filePath, patch, lockFields, ct);

            if (_scheduledJobsHostedService is not null)
            {
                // A backfill that is already running read its batch before this
                // edit, so ask for one more run after it instead of being dropped.
                _ = _scheduledJobsHostedService.RunNowOrAfterCurrentRunAsync(MetadataBackfillJobHandler.Key, CancellationToken.None);
            }

            var updated = await _fileStore.GetFileAsync(filePath, ct);
            return Accepted(new { filePath, metadataVersion = updated?.MetadataVersion ?? 0 });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Cannot patch metadata for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return NotFound("File is not tracked");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error patching metadata for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return StatusCode(500, "Error patching metadata");
        }
    }

    private static ComicMetadataFieldFlags DetermineEditedFields(ComicMetadata metadata)
    {
        var flags = ComicMetadataFieldFlags.None;
        if (metadata.Series is not null) flags |= ComicMetadataFieldFlags.Series;
        if (metadata.Title is not null) flags |= ComicMetadataFieldFlags.Title;
        if (metadata.Issue is not null) flags |= ComicMetadataFieldFlags.Issue;
        if (metadata.Volume is not null) flags |= ComicMetadataFieldFlags.Volume;
        if (metadata.Publisher is not null) flags |= ComicMetadataFieldFlags.Publisher;
        if (metadata.Year.HasValue) flags |= ComicMetadataFieldFlags.Year;
        if (metadata.Summary is not null) flags |= ComicMetadataFieldFlags.Summary;
        if (metadata.Authors.Count > 0) flags |= ComicMetadataFieldFlags.Authors;
        if (metadata.Tags.Count > 0) flags |= ComicMetadataFieldFlags.Tags;
        return flags;
    }

    private static ComicMetadataFieldFlags ParseMetadataField(string? field)
    {
        if (string.IsNullOrWhiteSpace(field)) return ComicMetadataFieldFlags.None;
        return field.Trim().ToLowerInvariant() switch
        {
            "series" => ComicMetadataFieldFlags.Series,
            "title" => ComicMetadataFieldFlags.Title,
            "issue" or "number" => ComicMetadataFieldFlags.Issue,
            "volume" => ComicMetadataFieldFlags.Volume,
            "publisher" => ComicMetadataFieldFlags.Publisher,
            "year" => ComicMetadataFieldFlags.Year,
            "summary" or "description" => ComicMetadataFieldFlags.Summary,
            "authors" or "writer" or "writers" => ComicMetadataFieldFlags.Authors,
            "tags" => ComicMetadataFieldFlags.Tags,
            _ => ComicMetadataFieldFlags.None
        };
    }

    [HttpGet("~/api/files/{encodedFilePath}/tags")]
    public async Task<ActionResult<ComicMetadata>> GetFileTags(string encodedFilePath)
    {
        try
        {
            // Decode the base64 URL-safe encoded file path
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to get tags for file outside watched directory: {EncodedPath}", LoggingHelper.SanitizeForLog(encodedFilePath));
                return BadRequest("File path is outside the allowed directory");
            }

            var metadata = await _processor.GetMetadataAsync(filePath);
            return metadata != null ? Ok(metadata) : NotFound();
        }
        catch (Exception ex)
        {
            var sanitizedEncodedPath = LoggingHelper.SanitizeForLog(encodedFilePath);
            _logger.LogError(ex, "Error getting tags for encoded path {EncodedPath}", sanitizedEncodedPath);
            return StatusCode(500, "Error getting tags");
        }
    }

    [HttpPut("~/api/files/{encodedFilePath}/tags")]
    public async Task<ActionResult> UpdateFileTags(string encodedFilePath, [FromBody] ComicMetadata metadata)
    {
        try
        {
            // Decode the base64 URL-safe encoded file path
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to update tags for file outside watched directory: {EncodedPath}", LoggingHelper.SanitizeForLog(encodedFilePath));
                return BadRequest("File path is outside the allowed directory");
            }

            var success = await _processor.UpdateMetadataAsync(filePath, metadata);
            if (success && _eventBroadcaster is not null)
            {
                try { await _eventBroadcaster.BroadcastFileProcessedAsync(Path.GetFileName(filePath), true); }
                catch (Exception bex) { _logger.LogDebug(bex, "Failed to broadcast file_processed for tag update"); }
            }
            return success ? Ok() : BadRequest("Failed to update tags");
        }
        catch (Exception ex)
        {
            var sanitizedEncodedPath = LoggingHelper.SanitizeForLog(encodedFilePath);
            _logger.LogError(ex, "Error updating tags for encoded path {EncodedPath}", sanitizedEncodedPath);
            return StatusCode(500, "Error updating tags");
        }
    }

    // RESTful endpoint: DELETE /api/files/{encodedFilePath}/metadata
    // Removes the embedded ComicInfo.xml from the archive and clears the
    // renamed/normalized/processed flags so the file will be re-evaluated on
    // the next processing run.
    [HttpDelete("~/api/files/{encodedFilePath}/metadata")]
    public async Task<ActionResult> RemoveFileMetadata(string encodedFilePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to remove metadata from file outside watched directory: {EncodedPath}", LoggingHelper.SanitizeForLog(encodedFilePath));
                return BadRequest("File path is outside the allowed directory");
            }

            if (!System.IO.File.Exists(filePath))
            {
                return NotFound();
            }

            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("Remove metadata requested for file: {FilePath}"), sanitizedPath);

            var success = await _processor.RemoveMetadataAsync(filePath, cancellationToken);
            if (!success)
            {
                await LogHistoryAsync(filePath, "RemoveMetadata", false, "RemoveMetadataAsync returned false");
                return BadRequest("Failed to remove metadata");
            }

            // Mark the file as unprocessed so the rename/normalize pipeline will
            // re-evaluate it on the next run.
            var cleared = await _fileStore.ClearProcessedStatusAsync(new[] { filePath }, cancellationToken);

            await LogHistoryAsync(filePath, "RemoveMetadata", true);

            // Broadcast a file_processed event so the UI updates the affected
            // folder / series detail in place rather than waiting for the
            // debounced file_list_updated.
            if (_eventBroadcaster is not null)
            {
                try { await _eventBroadcaster.BroadcastFileProcessedAsync(Path.GetFileName(filePath), true); }
                catch (Exception bex) { _logger.LogDebug(bex, "Failed to broadcast file_processed for metadata removal"); }
            }

            return Ok(new { success = true, cleared });
        }
        catch (Exception ex)
        {
            var sanitizedEncodedPath = LoggingHelper.SanitizeForLog(encodedFilePath);
            _logger.LogError(ex, "Error removing metadata from file with encoded path {EncodedPath}", sanitizedEncodedPath);

            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (!string.IsNullOrEmpty(filePath))
            {
                await LogHistoryAsync(filePath, "RemoveMetadata", false, ex.Message);
            }

            return StatusCode(500, "Error removing metadata");
        }
    }
}
