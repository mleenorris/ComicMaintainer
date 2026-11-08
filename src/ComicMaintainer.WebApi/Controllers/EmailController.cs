using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Authorization;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// Endpoints for emailing comics to saved ereader devices: device management,
/// one-off/bulk/series sends, and per-series automatic delivery.
/// </summary>
/// <remarks>
/// Devices, subscriptions and deliveries are private to the user who owns the
/// device: every request runs in the caller's <see cref="EmailAccessScope"/>,
/// and another user's device, subscription or delivery answers 404 exactly
/// like one that does not exist. Administrators can act on everyone's, and the
/// listings (and cancel-pending) include other users' when called with
/// <c>?all=true</c>.
/// </remarks>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class EmailController : ControllerBase
{
    private const int MaxSeriesIssuesPerSend = 1000;

    private readonly IEreaderDeviceService _devices;
    private readonly IComicEmailService _email;
    private readonly ICondensedBookBuildTracker _builds;
    private readonly ISeriesLibraryService _seriesLibrary;
    private readonly IAuthorizationService _authorization;
    private readonly IOptionsMonitor<AppSettings> _appSettings;
    private readonly ILogger<EmailController> _logger;

    public EmailController(
        IEreaderDeviceService devices,
        IComicEmailService email,
        ICondensedBookBuildTracker builds,
        ISeriesLibraryService seriesLibrary,
        IAuthorizationService authorization,
        IOptionsMonitor<AppSettings> appSettings,
        ILogger<EmailController> logger)
    {
        _devices = devices;
        _email = email;
        _builds = builds;
        _seriesLibrary = seriesLibrary;
        _authorization = authorization;
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

    /// <summary>
    /// The caller's devices; an administrator's call with <paramref name="all"/>
    /// lists every user's.
    /// </summary>
    [HttpGet("devices")]
    public async Task<ActionResult<object>> GetDevices(
        [FromQuery] bool all = false,
        CancellationToken cancellationToken = default)
    {
        if (await GetScopeAsync(all) is not { } scope)
        {
            return NoUser();
        }

        var devices = await _devices.GetDevicesAsync(scope, cancellationToken);
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

        if (await GetScopeAsync() is not { } scope)
        {
            return NoUser();
        }

        try
        {
            var device = await _devices.CreateDeviceAsync(
                scope,
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

        if (await GetScopeAsync() is not { } scope)
        {
            return NoUser();
        }

        try
        {
            var device = await _devices.UpdateDeviceAsync(
                scope,
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
        if (await GetScopeAsync() is not { } scope)
        {
            return NoUser();
        }

        var deleted = await _devices.DeleteDeviceAsync(scope, deviceId, cancellationToken);
        return deleted ? Ok(new { message = "Device deleted" }) : NotFound(new { error = "Device not found" });
    }

    /// <summary>Sends a short test message so the user can validate SMTP settings.</summary>
    [HttpPost("devices/{deviceId:int}/test")]
    public async Task<ActionResult> SendTestEmail(int deviceId, CancellationToken cancellationToken)
    {
        if (await GetScopeAsync() is not { } scope)
        {
            return NoUser();
        }

        try
        {
            await _email.SendTestEmailAsync(scope, deviceId, cancellationToken);
            return Ok(new { message = "Test email sent" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Test email to device {DeviceId} failed", deviceId);
            return StatusCode(502, new { error = $"Failed to send test email: {ex.Message}" });
        }
    }

    /// <summary>Queues one or more specific files for delivery.</summary>
    [HttpPost("send")]
    // Queueing walks and stats every file in the request, which for a bulk
    // selection is minutes of work rather than seconds.
    [RequestTimeout(RequestTimeoutPolicies.LongRunning)]
    public async Task<ActionResult<object>> SendFiles(
        [FromBody] SendFilesRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || request.Files is null || request.Files.Count == 0)
        {
            return BadRequest(new { error = "At least one file is required" });
        }

        if (await GetScopeAsync() is not { } scope)
        {
            return NoUser();
        }

        try
        {
            // Normalize first: an unrecognized mode must fail with 400 rather
            // than quietly falling through to separate per-issue emails.
            var condenseMode = EmailCondenseMode.NormalizeOrThrow(request.CondenseMode, nameof(request.CondenseMode));

            var result = EmailCondenseMode.IsCondensing(condenseMode)
                ? await _email.QueueCondensedFilesAsync(
                    scope,
                    request.Files,
                    request.DeviceId,
                    condenseMode,
                    request.IssuesPerBook,
                    EmailDeliverySource.Manual,
                    request.SkipAlreadyDelivered,
                    preserveIssueOrder: false,
                    request.DeliveryFormat,
                    cancellationToken)
                : await _email.QueueFilesAsync(
                    scope,
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
    [RequestTimeout(RequestTimeoutPolicies.LongRunning)]
    public async Task<ActionResult<object>> SendSeries(
        [FromBody] SendSeriesRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SeriesId))
        {
            return BadRequest(new { error = "Series id is required" });
        }

        if (await GetScopeAsync() is not { } scope)
        {
            return NoUser();
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
            // Normalize first: an unrecognized mode must fail with 400 rather
            // than quietly falling through to separate per-issue emails.
            var condenseMode = EmailCondenseMode.NormalizeOrThrow(request.CondenseMode, nameof(request.CondenseMode));

            var result = EmailCondenseMode.IsCondensing(condenseMode)
                ? await _email.QueueCondensedFilesAsync(
                    scope,
                    files,
                    request.DeviceId,
                    condenseMode,
                    request.IssuesPerBook,
                    EmailDeliverySource.Manual,
                    request.SkipAlreadyDelivered,
                    // GetSeriesIssuesAsync already returns one series in issue
                    // order; re-sorting it by folder would reorder the books.
                    preserveIssueOrder: true,
                    request.DeliveryFormat,
                    cancellationToken)
                : await _email.QueueFilesAsync(
                    scope,
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
    [RequestTimeout(RequestTimeoutPolicies.LongRunning)]
    public async Task<ActionResult<object>> PlanCondense(
        [FromBody] CondenseRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Request body is required" });
        }

        if (await GetScopeAsync() is not { } scope)
        {
            return NoUser();
        }

        var (files, error) = await ResolveCondenseFilesAsync(request, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        try
        {
            var plan = await _email.PlanCondensedDeliveryAsync(
                scope,
                files!,
                request.CondenseMode,
                request.IssuesPerBook,
                preserveIssueOrder: !string.IsNullOrWhiteSpace(request.SeriesId),
                request.DeviceId,
                request.SkipAlreadyDelivered,
                cancellationToken);

            return Ok(new
            {
                mode = plan.Mode,
                total_issues = plan.TotalIssues,
                issues_per_book = plan.IssuesPerBook,
                max_attachment_bytes = plan.MaxAttachmentBytes,
                max_book_bytes = EmailCondenseMode.MaxBookBytes,
                can_email = plan.CanEmail,
                oversized_book_count = plan.OversizedBookCount,
                books = plan.Books.Select(b => new
                {
                    display_name = b.DisplayName,
                    issue_count = b.Files.Count,
                    estimated_bytes = b.EstimatedBytes,
                    exceeds_attachment_limit = b.ExceedsAttachmentLimit,
                    exceeds_book_size_limit = b.ExceedsBookSizeLimit
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
    // Builds the entire book inside the request and then streams it: how long
    // that takes is a function of the series, not of anything we can bound.
    [DisableRequestTimeout]
    public async Task<ActionResult> DownloadCondensedBook(
        [FromBody] CondenseRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Request body is required" });
        }

        if (await GetScopeAsync() is not { } scope)
        {
            return NoUser();
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
                scope,
                files!,
                request.CondenseMode,
                request.IssuesPerBook,
                request.BookIndex,
                preserveIssueOrder: !string.IsNullOrWhiteSpace(request.SeriesId),
                request.DeviceId,
                request.SkipAlreadyDelivered,
                progress: null,
                request.DeliveryFormat,
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
        var stream = System.IO.File.Open(book.FilePath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose
        });

        Response.OnCompleted(() =>
        {
            TryDeleteDirectory(workDirectory);
            return Task.CompletedTask;
        });

        return File(stream, GetBookContentType(book.FileName), book.FileName);
    }

    /// <summary>
    /// Starts a condensed book build in the background and returns its id. The
    /// build outlives this request, so a book that takes minutes can be polled
    /// for progress with <see cref="GetCondenseBuild"/> and collected from
    /// <see cref="DownloadCondenseBuild"/> when it is done.
    /// </summary>
    [HttpPost("condense-builds")]
    // Resolving a whole series and queueing the build is bounded work, but at a
    // thousand issues it is not 30-second work.
    [RequestTimeout(RequestTimeoutPolicies.LongRunning)]
    public async Task<ActionResult<object>> StartCondenseBuild(
        [FromBody] CondenseRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Request body is required" });
        }

        if (await GetScopeAsync() is not { } scope)
        {
            return NoUser();
        }

        var (files, error) = await ResolveCondenseFilesAsync(request, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        try
        {
            var build = await _builds.StartAsync(
                new CondensedBookBuildRequest(
                    files!,
                    request.CondenseMode,
                    request.IssuesPerBook,
                    request.BookIndex,
                    PreserveIssueOrder: !string.IsNullOrWhiteSpace(request.SeriesId),
                    request.DeviceId,
                    request.SkipAlreadyDelivered,
                    request.DeliveryFormat),
                scope,
                cancellationToken);

            return Accepted(ToBuildResponse(build));
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

    /// <summary>Every condensed book build the caller has started recently.</summary>
    [HttpGet("condense-builds")]
    public ActionResult<object> GetCondenseBuilds()
    {
        var builds = _builds.List(CurrentUserId).Select(ToBuildResponse);
        return Ok(new { builds });
    }

    /// <summary>Progress and outcome of one condensed book build.</summary>
    [HttpGet("condense-builds/{buildId:guid}")]
    public ActionResult<object> GetCondenseBuild(Guid buildId)
    {
        var build = _builds.Get(buildId, CurrentUserId);
        return build is null
            ? NotFound(new { error = "Build not found" })
            : Ok(ToBuildResponse(build));
    }

    /// <summary>Streams the book a completed build produced.</summary>
    [HttpGet("condense-builds/{buildId:guid}/download")]
    // A condensed series can run to gigabytes; how long the client takes to
    // receive it is not something the server should cap.
    [DisableRequestTimeout]
    public ActionResult DownloadCondenseBuild(Guid buildId)
    {
        var build = _builds.Get(buildId, CurrentUserId);
        if (build is null)
        {
            return NotFound(new { error = "Build not found" });
        }

        var file = _builds.GetCompletedFile(buildId, CurrentUserId);
        if (file is null)
        {
            if (build.Status == CondensedBookBuildStatus.Completed)
            {
                // The build says it succeeded but its artifact is gone, so the
                // user is told to rebuild a book that was already built. That is
                // a defect on our side rather than a user error, and logging it
                // at Error is what gets it reported instead of silently
                // surfacing as a confusing 409.
                _logger.LogError(
                    "Condensed book build {BuildId} is marked completed but its file is missing; the user was asked to rebuild it.",
                    buildId);

                return Conflict(new { error = "The built book is no longer available; build it again." });
            }

            return Conflict(new
            {
                error = $"The book is not ready to download (status: {build.Status})."
            });
        }

        // The file is kept until the build expires or is discarded so an
        // interrupted download can simply be retried.
        var stream = System.IO.File.Open(file.FilePath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous
        });

        return File(stream, GetBookContentType(file.FileName), file.FileName);
    }

    /// <summary>Stops a running build.</summary>
    [HttpPost("condense-builds/{buildId:guid}/cancel")]
    public ActionResult<object> CancelCondenseBuild(Guid buildId)
    {
        if (_builds.Get(buildId, CurrentUserId) is null)
        {
            return NotFound(new { error = "Build not found" });
        }

        return _builds.Cancel(buildId, CurrentUserId)
            ? Ok(new { cancelled = true })
            : Conflict(new { error = "The build has already finished." });
    }

    /// <summary>Forgets a build and deletes anything it produced.</summary>
    [HttpDelete("condense-builds/{buildId:guid}")]
    public ActionResult DeleteCondenseBuild(Guid buildId)
    {
        return _builds.Discard(buildId, CurrentUserId)
            ? NoContent()
            : NotFound(new { error = "Build not found" });
    }

    private static object ToBuildResponse(CondensedBookBuildDto build) => new
    {
        build_id = build.BuildId.ToString(),
        status = build.Status,
        display_name = build.DisplayName,
        issue_count = build.IssueCount,
        created_at = build.CreatedAt,
        started_at = build.StartedAt,
        completed_at = build.CompletedAt,
        error = build.Error,
        file_size_bytes = build.FileSizeBytes,
        expires_at = build.ExpiresAt,
        delivery_format = build.DeliveryFormat,
        progress = new
        {
            phase = build.Progress.Phase,
            completed_pages = build.Progress.CompletedPages,
            total_pages = build.Progress.TotalPages,
            completed_issues = build.Progress.CompletedIssues,
            total_issues = build.Progress.TotalIssues,
            current_issue = build.Progress.CurrentIssue,
            pass = build.Progress.Pass,
            total_passes = build.Progress.TotalPasses,
            percentage = build.Progress.Percentage
        }
    };

    /// <summary>
    /// Builds are private to the user who started them: they are temporary
    /// files served back outside the library, so another account must not be
    /// able to enumerate or download them.
    /// </summary>
    private string? CurrentUserId => User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    /// <summary>
    /// The caller's access scope, or null when the request carries no user id
    /// (which no configured authentication scheme produces).
    /// </summary>
    /// <param name="listEveryone">
    /// Whether listings should include other users' rows; honoured for
    /// administrators only.
    /// </param>
    private async Task<EmailAccessScope?> GetScopeAsync(bool listEveryone = false)
    {
        var userId = CurrentUserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        var isAdministrator = (await _authorization.AuthorizeAsync(User, AuthorizationPolicies.CanAdminister)).Succeeded;
        return EmailAccessScope.ForUser(userId, isAdministrator, listEveryone);
    }

    /// <summary>
    /// Refuses a request without a user id: devices and deliveries are per
    /// user, so there is nothing such a caller could own.
    /// </summary>
    private ObjectResult NoUser() =>
        StatusCode(StatusCodes.Status403Forbidden, new { error = "Email delivery requires a signed-in user." });

    /// <summary>
    /// Content type of a generated condensed book, which is an EPUB or an AZW3
    /// depending on the requested delivery format.
    /// </summary>
    private static string GetBookContentType(string fileName) =>
        Path.GetExtension(fileName).Equals(".azw3", StringComparison.OrdinalIgnoreCase)
            ? "application/x-mobi8-ebook"
            : "application/epub+zip";

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

    /// <summary>
    /// The caller's subscriptions, optionally for one series; an
    /// administrator's call with <paramref name="all"/> lists every user's.
    /// </summary>
    [HttpGet("subscriptions")]
    public async Task<ActionResult<object>> GetSubscriptions(
        [FromQuery] string? series,
        [FromQuery] bool all = false,
        CancellationToken cancellationToken = default)
    {
        if (await GetScopeAsync(all) is not { } scope)
        {
            return NoUser();
        }

        var subscriptions = await _devices.GetSubscriptionsAsync(scope, series, cancellationToken);
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

        if (await GetScopeAsync() is not { } scope)
        {
            return NoUser();
        }

        try
        {
            var subscription = await _devices.UpsertSubscriptionAsync(
                scope,
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
        if (await GetScopeAsync() is not { } scope)
        {
            return NoUser();
        }

        var deleted = await _devices.DeleteSubscriptionAsync(scope, subscriptionId, cancellationToken);
        return deleted
            ? Ok(new { message = "Subscription removed" })
            : NotFound(new { error = "Subscription not found" });
    }

    /// <summary>
    /// One page of the caller's delivery history; an administrator's call with
    /// <paramref name="all"/> covers every user's.
    /// </summary>
    [HttpGet("deliveries")]
    public async Task<ActionResult<object>> GetDeliveries(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        [FromQuery] bool all = false,
        CancellationToken cancellationToken = default)
    {
        if (await GetScopeAsync(all) is not { } scope)
        {
            return NoUser();
        }

        var page = await _email.GetDeliveriesPageAsync(scope, offset, limit, cancellationToken);
        return Ok(new
        {
            deliveries = page.Deliveries,
            total = page.Total,
            offset = page.Offset,
            limit = page.Limit,
            pending_count = page.PendingCount,
            rate_limited_until = page.RateLimitedUntil
        });
    }

    /// <summary>
    /// Cancels a delivery that has not been sent yet. 202 when it is being
    /// prepared and has been asked to stop but has not stopped yet; 409 when it
    /// is already being sent or has finished.
    /// </summary>
    [HttpPost("deliveries/{deliveryId:int}/cancel")]
    public async Task<ActionResult<object>> CancelDelivery(int deliveryId, CancellationToken cancellationToken)
    {
        if (await GetScopeAsync() is not { } scope)
        {
            return NoUser();
        }

        var result = await _email.CancelDeliveryAsync(scope, deliveryId, cancellationToken);
        return result.Outcome switch
        {
            EmailDeliveryCancelOutcome.NotFound => NotFound(new { error = "Delivery not found" }),
            EmailDeliveryCancelOutcome.Cancelled => Ok(new { cancelled = true, delivery = result.Delivery }),
            EmailDeliveryCancelOutcome.Stopping => Accepted(new { cancelled = false, stopping = true, delivery = result.Delivery }),
            EmailDeliveryCancelOutcome.Sending => Conflict(new
            {
                error = "The delivery is already being sent and can no longer be cancelled.",
                delivery = result.Delivery
            }),
            _ => Conflict(new { error = DescribeFinishedDelivery(result.Delivery?.Status), delivery = result.Delivery }),
        };
    }

    /// <summary>
    /// Cancels every one of the caller's deliveries that has not been sent yet;
    /// an administrator's call with <paramref name="all"/> cancels every user's.
    /// </summary>
    [HttpPost("deliveries/cancel-pending")]
    public async Task<ActionResult<object>> CancelPendingDeliveries(
        [FromQuery] bool all = false,
        CancellationToken cancellationToken = default)
    {
        if (await GetScopeAsync(all) is not { } scope)
        {
            return NoUser();
        }

        var result = await _email.CancelPendingDeliveriesAsync(scope, cancellationToken);
        return Ok(new
        {
            cancelled = result.Cancelled,
            stopping = result.Stopping,
            sending = result.Sending
        });
    }

    private static string DescribeFinishedDelivery(string? status) => status switch
    {
        EmailDeliveryStatus.Sent => "The delivery has already been sent.",
        EmailDeliveryStatus.Failed => "The delivery has already failed.",
        EmailDeliveryStatus.Cancelled => "The delivery has already been cancelled.",
        _ => "The delivery has already finished.",
    };

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
        /// <see cref="IssuesPerBook"/> issues into one book, or <c>all</c> to
        /// condense the whole selection into a single book.
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

        /// <summary>
        /// Format the book is generated in: <c>epub</c> (the default) or
        /// <c>azw3</c>. <c>original</c> is rejected because the source archives
        /// cannot be merged.
        /// </summary>
        public string? DeliveryFormat { get; set; }

        /// <summary>
        /// Device the books would be sent to. Combined with
        /// <see cref="SkipAlreadyDelivered"/> it makes the plan describe exactly
        /// the books the send would queue.
        /// </summary>
        public int? DeviceId { get; set; }

        /// <summary>
        /// When true (the default), issues already pending or sent to
        /// <see cref="DeviceId"/> are left out of the books, matching the send.
        /// </summary>
        public bool SkipAlreadyDelivered { get; set; } = true;
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
