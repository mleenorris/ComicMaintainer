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
            var result = await _email.QueueFilesAsync(
                request.Files,
                request.DeviceId,
                request.DeliveryFormat,
                EmailDeliverySource.Manual,
                request.SkipAlreadyDelivered,
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

        var files = issues.Issues.Select(i => i.FilePath).ToList();
        if (files.Count == 0)
        {
            return BadRequest(new { error = "Series has no issues to send" });
        }

        try
        {
            var result = await _email.QueueFilesAsync(
                files,
                request.DeviceId,
                request.DeliveryFormat,
                EmailDeliverySource.Manual,
                request.SkipAlreadyDelivered,
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

        /// <summary>When true, files already delivered to the device are skipped.</summary>
        public bool SkipAlreadyDelivered { get; set; }
    }

    public class SendSeriesRequest
    {
        public string? SeriesId { get; set; }
        public int DeviceId { get; set; }
        public string? DeliveryFormat { get; set; }
        public bool SkipAlreadyDelivered { get; set; }
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
