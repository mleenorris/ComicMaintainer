using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// An issue's cover from ComicVine: the covers its volume has, and replacing the archive's cover
/// with one of them. Replacing needs library write access (see
/// <see cref="Authorization.WriteOperationAuthorizationConvention"/>).
/// </summary>
[ApiController]
[Route("api/covers")]
[Authorize]
public class CoversController : ControllerBase
{
    private readonly IComicVineIssueDetailsService _issueDetails;
    private readonly IIssueCoverService _covers;

    public CoversController(IComicVineIssueDetailsService issueDetails, IIssueCoverService covers)
    {
        _issueDetails = issueDetails;
        _covers = covers;
    }

    /// <summary>The ComicVine covers the file's cover can be replaced with.</summary>
    [HttpGet("candidates")]
    [FailureMessage("Error looking up covers on ComicVine")]
    public async Task<ActionResult<object>> Candidates([FromQuery] string? filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return BadRequest(new { error = "filePath is required" });
        }

        var candidates = await _issueDetails.GetCoverCandidatesAsync(filePath, cancellationToken);
        if (candidates is null)
        {
            return NotFound(new { error = "File not found" });
        }

        return Ok(new
        {
            volume_id = candidates.VolumeId,
            volume_name = candidates.VolumeName,
            matched_issue_id = candidates.MatchedIssueId,
            reason = candidates.Reason,
            covers = candidates.Covers.Select(cover => new
            {
                issue_id = cover.IssueId,
                issue_number = cover.IssueNumber,
                name = cover.Name,
                cover_date = cover.CoverDate,
                cover_url = cover.CoverUrl,
                thumb_url = cover.ThumbUrl,
                site_url = cover.SiteUrl,
                matched = cover.IssueId == candidates.MatchedIssueId
            })
        });
    }

    /// <summary>Makes the cover of ComicVine issue <c>comicVineIssueId</c> page 1 of the file's CBZ.</summary>
    [HttpPost("replace")]
    [FailureMessage("Error replacing the cover")]
    public async Task<ActionResult<object>> Replace([FromBody] ReplaceCoverRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.FilePath) || request.ComicVineIssueId <= 0)
        {
            return BadRequest(new { error = "filePath and comicVineIssueId are required" });
        }

        var result = await _covers.ReplaceWithComicVineCoverAsync(request.FilePath, request.ComicVineIssueId, cancellationToken);
        return result.Succeeded
            ? Ok(new { replaced = result.Changed })
            : BadRequest(new { error = result.Error });
    }

    /// <summary>The body of <c>POST /api/covers/replace</c>.</summary>
    public sealed class ReplaceCoverRequest
    {
        public string? FilePath { get; set; }
        public int ComicVineIssueId { get; set; }
    }
}
