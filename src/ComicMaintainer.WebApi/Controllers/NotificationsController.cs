using ComicMaintainer.Core.Notifications;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// Sends a target's test notification. Administrators only: targets may point at private
/// addresses, so nobody else may make the server call them.
/// </summary>
[ApiController]
[Route("api/notifications")]
[Authorize(Policy = AuthorizationPolicies.CanAdminister)]
public class NotificationsController : ControllerBase
{
    private readonly INotificationTester _tester;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(INotificationTester tester, ILogger<NotificationsController> logger)
    {
        _tester = tester;
        _logger = logger;
    }

    /// <summary>
    /// Sends a real test message to <paramref name="target"/>, using its saved settings, or asks
    /// a media server to scan. Answers 400 when the target's settings are incomplete, 409 when
    /// a scan was asked for while files are being changed, and 502 with the target's own error
    /// when it refused or could not be reached.
    /// </summary>
    [HttpPost("{target}/test")]
    public async Task<ActionResult> SendTest(string target, CancellationToken cancellationToken)
    {
        try
        {
            if (!await _tester.SendTestAsync(target, cancellationToken))
            {
                return Failure(StatusCodes.Status404NotFound, $"'{target}' is not a notification target.");
            }
        }
        catch (NotificationNotConfiguredException ex)
        {
            return Failure(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (NotificationLibraryBusyException ex)
        {
            return Failure(StatusCodes.Status409Conflict, ex.Message);
        }
        catch (NotificationDeliveryException ex)
        {
            // The whole point of a test, so a warning here rather than a defect to file.
            _logger.LogWarning("Test notification to {Target} failed: {Reason}", LoggingHelper.SanitizeForLog(target), ex.Message);
            return Failure(StatusCodes.Status502BadGateway, ex.Message);
        }

        return Ok(new { message = $"Test sent to {NotificationTarget.DisplayName(target)}." });
    }

    /// <summary>A problem document that also carries the <c>error</c> string every client of this API reads.</summary>
    private ObjectResult Failure(int status, string error)
    {
        var problem = new ProblemDetails { Status = status, Title = "The test notification was not sent.", Detail = error };
        problem.Extensions["error"] = error;
        return new ObjectResult(problem) { StatusCode = status };
    }
}
