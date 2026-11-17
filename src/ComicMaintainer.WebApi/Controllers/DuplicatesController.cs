using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// Reviewing a file flagged as a duplicate next to the file that has its name, and swapping the
/// two. Re-checking duplicates is a batch job: <c>POST /api/jobs/recheck-duplicates-{scope}</c>.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DuplicatesController : ControllerBase
{
    private const string FilePathRequiredError = "File path is required";
    private const string OutsideLibraryError = "File path is outside the allowed directory";

    private readonly IComicProcessorService _processor;
    private readonly IFileStoreService _fileStore;
    private readonly IComicReaderService _reader;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<DuplicatesController> _logger;

    public DuplicatesController(
        IComicProcessorService processor,
        IFileStoreService fileStore,
        IComicReaderService reader,
        IOptionsMonitor<AppSettings> settings,
        ILogger<DuplicatesController> logger)
    {
        _processor = processor;
        _fileStore = fileStore;
        _reader = reader;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// A file and the file that has the name processing would give it, side by side: what each
    /// is (size, date, pages, tags) and the name processing would give each now.
    /// </summary>
    [HttpGet("compare")]
    [FailureMessage("Error comparing the files")]
    public async Task<ActionResult<object>> Compare([FromQuery] string? filePath, CancellationToken cancellationToken)
    {
        if (ValidatePath(filePath) is { } invalid)
            return invalid;

        if (!System.IO.File.Exists(filePath))
            return NotFound(new { error = "File not found" });

        var clash = await _processor.FindDuplicateClashAsync(filePath!, cancellationToken);
        var file = await DescribeAsync(filePath!, clash, cancellationToken);
        object? other = null;
        if (clash.ClashingPath is { } clashingPath)
        {
            // The other file's own processed name says whether it really belongs there: an
            // Annual that took a regular issue's name would be renamed if processed again.
            var otherClash = await _processor.FindDuplicateClashAsync(clashingPath, cancellationToken);
            other = await DescribeAsync(clashingPath, otherClash, cancellationToken);
        }

        return Ok(new
        {
            status = clash.Status switch
            {
                DuplicateClashStatus.Clashes => "clashes",
                DuplicateClashStatus.Free => "free",
                _ => "unknown"
            },
            reason = clash.Reason,
            file,
            other
        });
    }

    /// <summary>
    /// Gives a duplicate the name the other file has and flags that other file as the duplicate.
    /// Nothing is deleted. <c>409</c> when the files are no longer as compared.
    /// </summary>
    [HttpPost("swap")]
    [FailureMessage("Error swapping the files")]
    public async Task<ActionResult<object>> Swap([FromBody] DuplicateSwapRequest request, CancellationToken cancellationToken)
    {
        if (ValidatePath(request.FilePath) is { } invalid)
            return invalid;

        var result = await _processor.SwapDuplicateAsync(request.FilePath!, cancellationToken);
        if (result.Succeeded)
        {
            _logger.LogInformation(
                LoggingHelper.WithWebsitePrefix("Duplicate swap: {FilePath} took the name; {DuplicatePath} is now the duplicate"),
                LoggingHelper.SanitizePathForLog(result.ProperPath ?? string.Empty),
                LoggingHelper.SanitizePathForLog(result.DuplicatePath ?? string.Empty));
            return Ok(new { success = true, file_path = result.ProperPath, duplicate_path = result.DuplicatePath });
        }

        return result.Conflict
            ? Conflict(new { error = result.Error })
            : BadRequest(new { error = result.Error });
    }

    /// <summary>The problem with a named file, or null when it can be used.</summary>
    private ActionResult? ValidatePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return BadRequest(new { error = FilePathRequiredError });

        var settings = _settings.CurrentValue;
        if (!LibraryPathValidator.TryResolveWithin(filePath, out _, settings.WatchedDirectory, settings.DuplicateDirectory))
        {
            _logger.LogWarning(
                LoggingHelper.WithWebsitePrefix("Duplicate request names a file outside the allowed directories: {FilePath}"),
                LoggingHelper.SanitizePathForLog(filePath));
            return BadRequest(new { error = OutsideLibraryError });
        }

        return null;
    }

    private async Task<object> DescribeAsync(string filePath, DuplicateClash clash, CancellationToken cancellationToken)
    {
        var info = new FileInfo(filePath);
        var tracked = await _fileStore.GetFileAsync(filePath, cancellationToken);
        var metadata = await _processor.GetMetadataAsync(filePath, cancellationToken);
        return new
        {
            file_path = filePath,
            name = Path.GetFileName(filePath),
            size = info.Exists ? info.Length : (long?)null,
            modified = info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds() : (long?)null,
            page_count = await CountPagesAsync(filePath),
            duplicate = tracked?.IsDuplicate ?? false,
            processed_name = clash.TargetPath is null ? null : Path.GetFileName(clash.TargetPath),
            tags = metadata is null ? null : new
            {
                series = metadata.Series,
                number = metadata.Issue,
                volume = metadata.Volume,
                volume_year = metadata.VolumeYear,
                format = metadata.Format,
                title = metadata.Title,
                year = metadata.Year
            }
        };
    }

    // An archive the reader cannot list (over its limits, damaged) has no page count to show.
    private async Task<int?> CountPagesAsync(string filePath)
    {
        try
        {
            var count = await _reader.GetPageCountAsync(filePath);
            return count > 0 ? count : null;
        }
        catch (ArchiveLimitExceededException)
        {
            return null;
        }
    }

    /// <summary>The body of <c>POST /api/duplicates/swap</c>.</summary>
    public sealed class DuplicateSwapRequest
    {
        /// <summary>The file flagged as a duplicate, which is to take the other file's name.</summary>
        public string? FilePath { get; set; }
    }
}
