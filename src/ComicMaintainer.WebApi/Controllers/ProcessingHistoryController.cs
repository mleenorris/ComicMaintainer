using ComicMaintainer.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/processing-history")]
[Authorize]
public class ProcessingHistoryController : ControllerBase
{
    private readonly IProcessingHistoryService _historyService;
    private readonly ILogger<ProcessingHistoryController> _logger;

    public ProcessingHistoryController(
        IProcessingHistoryService historyService,
        ILogger<ProcessingHistoryController> logger)
    {
        _historyService = historyService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<object>> GetProcessingHistory(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (history, total) = await _historyService.GetHistoryAsync(limit, offset, cancellationToken);

            // Convert to frontend format
            var historyItems = history.Select(h => new
            {
                timestamp = new DateTimeOffset(DateTime.SpecifyKind(h.Timestamp, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                filepath = h.FilePath,
                operation_type = h.Action,
                success = h.Success,
                error_message = h.ErrorMessage,
                before_filename = h.BeforeFilename,
                after_filename = h.AfterFilename,
                before_title = h.BeforeTitle,
                after_title = h.AfterTitle,
                before_series = h.BeforeSeries,
                after_series = h.AfterSeries,
                before_issue = h.BeforeIssue,
                after_issue = h.AfterIssue,
                before_publisher = h.BeforePublisher,
                after_publisher = h.AfterPublisher,
                before_year = h.BeforeYear,
                after_year = h.AfterYear,
                before_volume = h.BeforeVolume,
                after_volume = h.AfterVolume
            });

            return Ok(new
            {
                history = historyItems,
                total
            });
        }
        catch (ArgumentOutOfRangeException ex)
        {
            _logger.LogWarning(ex, "Invalid request parameters for processing history");
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving processing history");
            return StatusCode(500, "Error retrieving processing history");
        }
    }
}
