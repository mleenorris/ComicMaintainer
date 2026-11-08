using ComicMaintainer.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Infrastructure;

/// <summary>
/// Maps a <see cref="StaleCleanupResult"/> to the HTTP response of the manual cleanup
/// endpoints, so both report the guards the same way.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>200: the cleanup ran (<c>removedCount</c> rows were deleted).</item>
/// <item>409 with <c>requiresConfirmation = true</c>: the watched directory holds no comics,
/// more than the configured share of the library would be removed, or files are missing
/// that have not yet stayed missing for the grace period. Nothing was removed; repeat the
/// request with <c>?confirm=true</c> to go ahead.</item>
/// <item>409 with <c>requiresConfirmation = false</c>: the watched directory or the sentinel
/// file is missing; confirmation cannot override that, mount the library first.</item>
/// <item>499: the request was cancelled; 500: the cleanup failed. Nothing was removed.</item>
/// </list>
/// </remarks>
internal static class StaleCleanupResponses
{
    public static ActionResult ToActionResult(ControllerBase controller, StaleCleanupResult result)
    {
        var body = new
        {
            success = result.Status == StaleCleanupStatus.Completed,
            status = result.Status.ToString(),
            requiresConfirmation = result.RequiresConfirmation,
            message = result.Message,
            removedCount = result.RemovedCount,
            trackedCount = result.TrackedCount,
            missingCount = result.MissingCount,
            removableCount = result.RemovableCount,
            uncheckedCount = result.UncheckedCount,
            maxRemovalPercent = result.MaxRemovalPercent,
            error = result.Status == StaleCleanupStatus.Completed ? null : result.Message,
        };

        return result.Status switch
        {
            StaleCleanupStatus.Completed => controller.Ok(body),
            StaleCleanupStatus.Cancelled => controller.StatusCode(499, body),
            StaleCleanupStatus.Failed => controller.StatusCode(StatusCodes.Status500InternalServerError, body),
            _ => controller.Conflict(body),
        };
    }
}
