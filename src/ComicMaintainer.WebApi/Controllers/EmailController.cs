using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// Endpoints for emailing comics to saved ereader devices: device management,
/// one-off/bulk/series sends, and per-series automatic delivery.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class EmailController : ControllerBase
{
    private const int MaxSeriesIssuesPerSend = 1000;

    private readonly IEreaderDeviceService _devices;
    private readonly IComicEmailService _email;
    private readonly ISeriesLibraryService _seriesLibrary;
    private readonly IOptionsMonitor<AppSettings> _appSettings;
    private readonly ILogger<EmailController> _logger;

    public EmailController(
        IEreaderDeviceService devices,
        IComicEmailService email,
        ISeriesLibraryService seriesLibrary,
        IOptionsMonitor<AppSettings> appSettings,
        ILogger<EmailController> logger)
    {
        _devices = devices;
        _email = email;
        _seriesLibrary = seriesLibrary;
        _appSettings = appSettings;
        _logger = logger;
    }

    /// <summary>Whether email delivery is usable, for enabling/disabling UI actions.</summary>
    [HttpGet("status")]
    public ActionResult<object> GetStatus()
    {
        return Ok(new
        {
            configured = _email.IsConfigured,
            from_address = _appSettings.CurrentValue.EmailFromAddress,
            max_attachment_mb = _appSettings.CurrentValue.EmailMaxAttachmentMegabytes
        });
    }

    [HttpGet("devices")]
    public async Task<ActionResult<object>> GetDevices(CancellationToken cancellationToken)
    {
        var devices = await _devices.GetDevicesAsync(cancellationToken);
        return Ok(new { devices });
    }

    [HttpPost("devices")]
    public async Task<ActionResult<EreaderDeviceDto>> CreateDevice(
        [FromBody] DeviceRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Request body is required" });
        }

        try
        {
            var device = await _devices.CreateDeviceAsync(
                request.Name ?? string.Empty,
                request.EmailAddress ?? string.Empty,
                request.DeliveryFormat,
                cancellationToken);

            return Ok(device);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPut("devices/{deviceId:int}")]
    public async Task<ActionResult<EreaderDeviceDto>> UpdateDevice(
        int deviceId,
        [FromBody] DeviceRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Request body is required" });
        }

        try
        {
            var device = await _devices.UpdateDeviceAsync(
                deviceId,
                request.Name,
                request.EmailAddress,
                request.DeliveryFormat,
                cancellationToken);

            return device is null ? NotFound(new { error = "Device not found" }) : Ok(device);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpDelete("devices/{deviceId:int}")]
    public async Task<ActionResult> DeleteDevice(int deviceId, CancellationToken cancellationToken)
    {
        var deleted = await _devices.DeleteDeviceAsync(deviceId, cancellationToken);
        return deleted ? Ok(new { message = "Device deleted" }) : NotFound(new { error = "Device not found" });
    }

    /// <summary>Sends a short test message so the user can validate SMTP settings.</summary>
    [HttpPost("devices/{deviceId:int}/test")]
    public async Task<ActionResult> SendTestEmail(int deviceId, CancellationToken cancellationToken)
    {
        try
        {
            await _email.SendTestEmailAsync(deviceId, cancellationToken);
            return Ok(new { message = "Test email sent" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test email to device {DeviceId} failed", deviceId);
            return StatusCode(502, new { error = $"Failed to send test email: {ex.Message}" });
        }
    }

    /// <summary>Queues one or more specific files for delivery.</summary>
    [HttpPost("send")]
    public async Task<ActionResult<object>> SendFiles(
        [FromBody] SendFilesRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || request.Files is null || request.Files.Count == 0)
        {
            return BadRequest(new { error = "At least one file is required" });
        }

        try
        {
            var result = EmailCondenseMode.IsCondensing(request.CondenseMode)
                ? await _email.QueueCondensedFilesAsync(
                    request.Files,
                    request.DeviceId,
                    request.CondenseMode,
                    request.IssuesPerBook,
                    EmailDeliverySource.Manual,
                    request.SkipAlreadyDelivered,
                    cancellationToken)
                : await _email.QueueFilesAsync(
                    request.Files,
                    request.DeviceId,
                    request.DeliveryFormat,
                    EmailDeliverySource.Manual,
                    request.SkipAlreadyDelivered,
                    subscriptionId: null,
                    cancellationToken);

            return Ok(new { queued = result.Queued, skipped = result.Skipped });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Queues every issue of a series for delivery.</summary>
    [HttpPost("send-series")]
    public async Task<ActionResult<object>> SendSeries(
        [FromBody] SendSeriesRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SeriesId))
        {
            return BadRequest(new { error = "Series id is required" });
        }

        var issues = await _seriesLibrary.GetSeriesIssuesAsync(
            request.SeriesId,
            filter: null,
            page: 1,
            perPage: MaxSeriesIssuesPerSend,
            cancellationToken);

        if (issues is null)
        {
            return NotFound(new { error = "Series not found" });
        }

        // Never silently truncate: a series larger than one page would drop
        // issues from the send without the caller noticing.
        if (issues.IssueCount > MaxSeriesIssuesPerSend || issues.TotalPages > 1)
        {
            return BadRequest(new
            {
                error = $"Series has {issues.IssueCount} issues, which exceeds the {MaxSeriesIssuesPerSend} issue limit for a single send. Select the issues to send instead."
            });
        }

        var files = issues.Issues.Select(i => i.FilePath).ToList();
        if (files.Count == 0)
        {
            return BadRequest(new { error = "Series has no issues to send" });
        }

        try
        {
            var result = EmailCondenseMode.IsCondensing(request.CondenseMode)
                ? await _email.QueueCondensedFilesAsync(
                    files,
                    request.DeviceId,
                    request.CondenseMode,
                    request.IssuesPerBook,
                    EmailDeliverySource.Manual,
                    request.SkipAlreadyDelivered,
                    cancellationToken)
                : await _email.QueueFilesAsync(
                    files,
                    request.DeviceId,
                    request.DeliveryFormat,
                    EmailDeliverySource.Manual,
                    request.SkipAlreadyDelivered,
                    subscriptionId: null,
                    cancellationToken);

            return Ok(new
            {
                series = issues.Title,
                queued = result.Queued,
                skipped = result.Skipped
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Describes what condensing a selection (or a whole series) would produce:
    /// the books, their estimated size and whether any of them is too large to
    /// email and has to be downloaded instead.
    /// </summary>
    [HttpPost("condense-plan")]
    public async Task<ActionResult<object>> PlanCondense(
        [FromBody] CondenseRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Request body is required" });
        }

        var (files, error) = await ResolveCondenseFilesAsync(request, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        try
        {
            var plan = await _email.PlanCondensedDeliveryAsync(
                files!,
                request.CondenseMode,
                request.IssuesPerBook,
                cancellationToken);

            return Ok(new
            {
                mode = plan.Mode,
                total_issues = plan.TotalIssues,
                issues_per_book = plan.IssuesPerBook,
                max_attachment_bytes = plan.MaxAttachmentBytes,
                can_email = plan.CanEmail,
                oversized_book_count = plan.OversizedBookCount,
                books = plan.Books.Select(b => new
                {
                    display_name = b.DisplayName,
                    issue_count = b.Files.Count,
                    estimated_bytes = b.EstimatedBytes,
                    exceeds_attachment_limit = b.ExceedsAttachmentLimit
                }),
                skipped = plan.Skipped
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Builds one condensed book and streams it back, for books that are too
    /// large to email.
    /// </summary>
    [HttpPost("condense-download")]
    public async Task<ActionResult> DownloadCondensedBook(
        [FromBody] CondenseRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Request body is required" });
        }

        var (files, error) = await ResolveCondenseFilesAsync(request, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        CondensedBookFile book;
        try
        {
            book = await _email.CreateCondensedBookAsync(
                files!,
                request.CondenseMode,
                request.IssuesPerBook,
                request.BookIndex,
                cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to build a condensed book for download");
            return StatusCode(500, new { error = $"Failed to build the condensed book: {ex.Message}" });
        }

        // The generated book only exists for this response: delete it (and the
        // work directory it lives in) as soon as it has been streamed.
        var workDirectory = Path.GetDirectoryName(book.FilePath);
        var stream = new FileStream(
            book.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);

        Response.OnCompleted(() =>
        {
            TryDeleteDirectory(workDirectory);
            return Task.CompletedTask;
        });

        return File(stream, "application/epub+zip", book.FileName);
    }

    /// <summary>
    /// Resolves the issues a condense request targets: either the explicit file
    /// list or every issue of the requested series.
    /// </summary>
    private async Task<(List<string>? Files, ActionResult? Error)> ResolveCondenseFilesAsync(
        CondenseRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.SeriesId))
        {
            var issues = await _seriesLibrary.GetSeriesIssuesAsync(
                request.SeriesId,
                filter: null,
                page: 1,
                perPage: MaxSeriesIssuesPerSend,
                cancellationToken);

            if (issues is null)
            {
                return (null, NotFound(new { error = "Series not found" }));
            }

            // Never silently truncate: a series larger than one page would drop
            // issues from the condensed books without the caller noticing.
            if (issues.IssueCount > MaxSeriesIssuesPerSend || issues.TotalPages > 1)
            {
                return (null, BadRequest(new
                {
                    error = $"Series has {issues.IssueCount} issues, which exceeds the {MaxSeriesIssuesPerSend} issue limit for a single send. Select the issues to send instead."
                }));
            }

            var seriesFiles = issues.Issues.Select(i => i.FilePath).ToList();
            return seriesFiles.Count == 0
                ? (null, BadRequest(new { error = "Series has no issues to send" }))
                : (seriesFiles, null);
        }

        if (request.Files is null || request.Files.Count == 0)
        {
            return (null, BadRequest(new { error = "At least one file is required" }));
        }

        return (request.Files, null);
    }

    private void TryDeleteDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to clean up the condensed book work directory");
        }
    }

    [HttpGet("subscriptions")]
    public async Task<ActionResult<object>> GetSubscriptions(
        [FromQuery] string? series,
        CancellationToken cancellationToken)
    {
        var subscriptions = await _devices.GetSubscriptionsAsync(series, cancellationToken);
        return Ok(new { subscriptions });
    }

    /// <summary>Creates or updates the automatic-delivery subscription for a series.</summary>
    [HttpPut("subscriptions")]
    public async Task<ActionResult<SeriesEmailSubscriptionDto>> UpsertSubscription(
        [FromBody] SubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SeriesTitle))
        {
            return BadRequest(new { error = "Series title is required" });
        }

        try
        {
            var subscription = await _devices.UpsertSubscriptionAsync(
                request.SeriesTitle,
                request.DeviceId,
                request.DeliveryFormat,
                request.Enabled,
                cancellationToken);

            return subscription is null
                ? NotFound(new { error = "Device not found" })
                : Ok(subscription);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("subscriptions/{subscriptionId:int}")]
    public async Task<ActionResult> DeleteSubscription(int subscriptionId, CancellationToken cancellationToken)
    {
        var deleted = await _devices.DeleteSubscriptionAsync(subscriptionId, cancellationToken);
        return deleted
            ? Ok(new { message = "Subscription removed" })
            : NotFound(new { error = "Subscription not found" });
    }

    [HttpGet("deliveries")]
    public async Task<ActionResult<object>> GetDeliveries(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var deliveries = await _email.GetRecentDeliveriesAsync(limit, cancellationToken);
        return Ok(new { deliveries });
    }

    public class DeviceRequest
    {
        public string? Name { get; set; }
        public string? EmailAddress { get; set; }

        /// <summary><c>original</c> or <c>epub</c>.</summary>
        public string? DeliveryFormat { get; set; }
    }

    public class SendFilesRequest
    {
        public List<string> Files { get; set; } = new();
        public int DeviceId { get; set; }

        /// <summary>Overrides the device default when set.</summary>
        public string? DeliveryFormat { get; set; }

        /// <summary>
        /// When true (the default), files that already have a pending or sent
        /// delivery for the device are skipped.
        /// </summary>
        public bool SkipAlreadyDelivered { get; set; } = true;

        /// <summary>
        /// <c>none</c> (default), <c>count</c> to condense every
        /// <see cref="IssuesPerBook"/> issues into one EPUB, or <c>all</c> to
        /// condense the whole selection into a single EPUB.
        /// </summary>
        public string? CondenseMode { get; set; }

        /// <summary>Issues per condensed book; required when the mode is <c>count</c>.</summary>
        public int? IssuesPerBook { get; set; }
    }

    public class SendSeriesRequest
    {
        public string? SeriesId { get; set; }
        public int DeviceId { get; set; }
        public string? DeliveryFormat { get; set; }

        /// <summary>
        /// When true (the default), files that already have a pending or sent
        /// delivery for the device are skipped.
        /// </summary>
        public bool SkipAlreadyDelivered { get; set; } = true;

        /// <summary><c>none</c> (default), <c>count</c> or <c>all</c>.</summary>
        public string? CondenseMode { get; set; }

        /// <summary>Issues per condensed book; required when the mode is <c>count</c>.</summary>
        public int? IssuesPerBook { get; set; }
    }

    public class CondenseRequest
    {
        /// <summary>Files to condense. Ignored when <see cref="SeriesId"/> is set.</summary>
        public List<string> Files { get; set; } = new();

        /// <summary>Condense every issue of this series instead of <see cref="Files"/>.</summary>
        public string? SeriesId { get; set; }

        /// <summary><c>count</c> or <c>all</c>.</summary>
        public string? CondenseMode { get; set; }

        /// <summary>Issues per condensed book; required when the mode is <c>count</c>.</summary>
        public int? IssuesPerBook { get; set; }

        /// <summary>Zero-based index of the book to download (download endpoint only).</summary>
        public int BookIndex { get; set; }
    }

    public class SubscriptionRequest
    {
        public string? SeriesTitle { get; set; }
        public int DeviceId { get; set; }

        /// <summary><c>original</c>, <c>epub</c>, or <c>device</c> to inherit the device default.</summary>
        public string? DeliveryFormat { get; set; }

        public bool Enabled { get; set; } = true;
    }
}
