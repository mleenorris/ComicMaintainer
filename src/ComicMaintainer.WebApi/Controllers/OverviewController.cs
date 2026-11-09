using System.Security.Claims;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Infrastructure;
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

    public OverviewController(IOverviewService overview)
    {
        _overview = overview;
    }

    [HttpGet]
    [FailureMessage("Error building overview")]
    public async Task<ActionResult<OverviewResult>> Get(CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var result = await _overview.GetOverviewAsync(userId, cancellationToken);
        return Ok(result);
    }
}
