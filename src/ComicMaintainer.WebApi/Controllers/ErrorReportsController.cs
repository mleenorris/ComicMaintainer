using System.ComponentModel.DataAnnotations;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.ErrorReporting.Interfaces;
using ComicMaintainer.Core.ErrorReporting.Models;
using ComicMaintainer.WebApi.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// Review and delivery surface for captured errors.
/// </summary>
/// <remarks>
/// Read and delivery actions require administrator rights because reports carry
/// diagnostic detail about the instance. The single exception is
/// <see cref="ReportClientError"/>, which any signed-in user's browser must be
/// able to call to report a frontend failure; it is marked
/// <see cref="PerUserWriteOperationAttribute"/> so
/// <c>WriteOperationAuthorizationConvention</c> does not require
/// <c>CanAdminister</c> for it.
/// </remarks>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ErrorReportsController : ControllerBase
{
    private readonly IErrorReportService _errorReports;
    private readonly IOptionsMonitor<AppSettings> _settings;

    public ErrorReportsController(
        IErrorReportService errorReports,
        IOptionsMonitor<AppSettings> settings)
    {
        _errorReports = errorReports;
        _settings = settings;
    }

    /// <summary>Returns the stored reports, newest occurrence first.</summary>
    [HttpGet]
    [Authorize(AuthorizationPolicies.CanAdminister)]
    public async Task<ActionResult<object>> GetReports(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var settings = _settings.CurrentValue;
        var reports = await _errorReports.GetReportsAsync(Math.Clamp(limit, 1, 200), cancellationToken);

        return Ok(new
        {
            enabled = settings.EnableErrorReporting,
            mode = settings.ErrorReportingMode,
            reports,
        });
    }

    /// <summary>
    /// Returns the exact issue body that would be filed, plus the pre-filled
    /// GitHub URL for the consent flow.
    /// </summary>
    /// <remarks>
    /// Showing the payload before anything leaves the instance is a hard
    /// requirement of this feature, not a convenience: the user cannot give
    /// meaningful consent to a disclosure they have not seen.
    /// </remarks>
    [HttpGet("{fingerprint}/preview")]
    [Authorize(AuthorizationPolicies.CanAdminister)]
    public async Task<ActionResult<object>> PreviewReport(
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        var body = await _errorReports.PreviewIssueBodyAsync(fingerprint, cancellationToken);
        if (body is null)
        {
            return NotFound(new { message = "No stored report with that fingerprint." });
        }

        var report = await _errorReports.GetReportAsync(fingerprint, cancellationToken);

        return Ok(new { fingerprint, body, report });
    }

    /// <summary>
    /// Delivers a stored report through the configured transport.
    /// </summary>
    /// <remarks>
    /// In the default consent mode nothing is transmitted: the response carries
    /// the pre-filled issue URL for the user to open and submit themselves.
    /// </remarks>
    [HttpPost("{fingerprint}/submit")]
    [Authorize(AuthorizationPolicies.CanAdminister)]
    public async Task<ActionResult<object>> SubmitReport(
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.CurrentValue.EnableErrorReporting)
        {
            return BadRequest(new { message = "Error reporting is disabled in settings." });
        }

        var result = await _errorReports.SubmitAsync(fingerprint, cancellationToken);

        return Ok(new
        {
            delivered = result.Delivered,
            issueNumber = result.IssueNumber,
            issueUrl = result.IssueUrl,
            message = result.Detail,
        });
    }

    /// <summary>Discards a stored report so a recurrence is captured afresh.</summary>
    [HttpDelete("{fingerprint}")]
    [Authorize(AuthorizationPolicies.CanAdminister)]
    public async Task<IActionResult> DismissReport(
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        var removed = await _errorReports.DismissAsync(fingerprint, cancellationToken);
        return removed ? NoContent() : NotFound();
    }

    /// <summary>
    /// Records a browser-side error reported by <c>window.onerror</c> or an
    /// unhandled promise rejection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The payload is attacker-controlled — any signed-in user can post
    /// anything here. It is therefore length-capped on the way in, redacted
    /// like every other report, and never auto-transmitted: a frontend report
    /// still has to pass the same consent or opt-in gate as the rest.
    /// </para>
    /// <para>
    /// The response is always 202 regardless of whether the report was stored
    /// or suppressed, so the page cannot use this endpoint to probe which
    /// errors the instance is tracking.
    /// </para>
    /// </remarks>
    [HttpPost("client")]
    [PerUserWriteOperation]
    public async Task<IActionResult> ReportClientError(
        [FromBody] ClientErrorReportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return BadRequest();
        }

        if (_settings.CurrentValue.EnableErrorReporting)
        {
            await _errorReports.CaptureAsync(
                string.IsNullOrWhiteSpace(request.Name) ? "Error" : request.Name,
                request.Message,
                request.Stack,
                ErrorReportSource.Frontend,
                request.Page,
                lastUserAction: request.LastUserAction,
                cancellationToken: cancellationToken);
        }

        return Accepted();
    }

    /// <summary>Browser-supplied error details.</summary>
    public sealed class ClientErrorReportRequest
    {
        /// <summary>Error constructor name, e.g. <c>TypeError</c>.</summary>
        [MaxLength(200)]
        public string? Name { get; set; }

        /// <summary>Error message.</summary>
        [MaxLength(2000)]
        public string? Message { get; set; }

        /// <summary>Browser stack trace.</summary>
        [MaxLength(8000)]
        public string? Stack { get; set; }

        /// <summary>Page the error occurred on, e.g. <c>/reader.html</c>.</summary>
        [MaxLength(500)]
        public string? Page { get; set; }

        /// <summary>Short description of the last UI action before the failure.</summary>
        [MaxLength(500)]
        public string? LastUserAction { get; set; }
    }
}
