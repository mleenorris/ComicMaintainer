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
                timestamp = new DateTimeOffset(h.Timestamp).ToUnixTimeSeconds(),
                filepath = h.FilePath,
                operation_type = h.Action,
                success = h.Success,
                error_message = h.ErrorMessage,
                // These fields are expected by the frontend but not yet implemented
                // They will be shown as empty for now
                before_filename = (string?)null,
                after_filename = (string?)null,
                before_title = (string?)null,
                after_title = (string?)null,
                before_series = (string?)null,
                after_series = (string?)null,
                before_issue = (string?)null,
                after_issue = (string?)null,
                before_publisher = (string?)null,
                after_publisher = (string?)null,
                before_year = (int?)null,
                after_year = (int?)null,
                before_volume = (string?)null,
                after_volume = (string?)null
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
