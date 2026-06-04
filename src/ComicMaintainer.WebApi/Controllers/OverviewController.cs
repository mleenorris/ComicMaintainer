using System.Security.Claims;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// Serves the overview/home page rows (Continue Reading, Series Updates,
/// Newly Added Series). Continue Reading is scoped to the authenticated user.
/// </summary>
[ApiController]
[Route("api/overview")]
[Authorize]
public class OverviewController : ControllerBase
{
    private readonly IOverviewService _overview;
    private readonly ILogger<OverviewController> _logger;

    public OverviewController(IOverviewService overview, ILogger<OverviewController> logger)
    {
        _overview = overview;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<OverviewResult>> Get(CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        try
        {
            var result = await _overview.GetOverviewAsync(userId, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building overview page");
            return StatusCode(500, new { error = "Error building overview" });
        }
    }
}
