using ComicMaintainer.Core.Services;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// Editing a comic's pages: removing scan credits, ads or a duplicated cover from its CBZ.
/// Needs library write access (see <see cref="Authorization.WriteOperationAuthorizationConvention"/>).
/// </summary>
[ApiController]
[Route("api/pages")]
[Authorize]
public class ComicPagesController : ControllerBase
{
    /// <summary>Most pages one request may name; a comic with more is edited in steps.</summary>
    public const int MaxPagesPerRequest = 2000;

    private readonly IComicPageRemover _remover;

    public ComicPagesController(IComicPageRemover remover)
    {
        _remover = remover;
    }

    /// <summary>
    /// Removes the pages named in the body (entry names, as <c>GET /api/comicreader/pages</c>
    /// lists them) from the file's CBZ. <c>200</c> with how many were removed and are left;
    /// <c>400</c> with the reason nothing was.
    /// </summary>
    [HttpPost("remove")]
    [FailureMessage("Error removing pages")]
    public async Task<ActionResult<object>> Remove([FromBody] RemovePagesRequest? request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.FilePath) || request.Pages is not { Count: > 0 } pages)
        {
            return BadRequest(new { error = "filePath and pages are required" });
        }

        if (pages.Count > MaxPagesPerRequest)
        {
            return BadRequest(new { error = $"At most {MaxPagesPerRequest} pages can be removed at once" });
        }

        var result = await _remover.RemovePagesAsync(request.FilePath, pages, cancellationToken);
        return result.Succeeded
            ? Ok(new { removed = result.Removed, pages_left = result.PagesLeft })
            : BadRequest(new { error = result.Error });
    }

    /// <summary>The body of <c>POST /api/pages/remove</c>.</summary>
    public sealed class RemovePagesRequest
    {
        public string? FilePath { get; set; }
        public List<string>? Pages { get; set; }
    }
}
