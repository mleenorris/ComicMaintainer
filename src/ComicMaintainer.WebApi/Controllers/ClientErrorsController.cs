using System.ComponentModel.DataAnnotations;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.ErrorReporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// Intake for failures that happen in the browser.
/// </summary>
/// <remarks>
/// <para>Automated error reporting is wired to the server's logging pipeline, so
/// it only ever saw server-side failures. A defect in the front end — a handler
/// that throws, a promise nobody awaits, markup that produces an uncompilable
/// inline handler — left the user looking at a broken page while the issue
/// tracker stayed silent, because nothing about the failure ever reached the
/// server. This endpoint closes that gap: the browser posts what it caught and
/// the report joins the same queue, dedupe, rate limit and GitHub issue path as
/// every server-side failure.</para>
/// <para>Open to any authenticated user, including <c>ReadOnly</c> ones: a
/// read-only user browsing the library hits front-end defects like anyone else,
/// and nothing here mutates the library. It is deliberately not listed in
/// <see cref="Authorization.WriteOperationAuthorizationConvention"/> for that
/// reason.</para>
/// </remarks>
[ApiController]
[Route("api/client-errors")]
[Authorize]
public class ClientErrorsController : ControllerBase
{
    /// <summary>
    /// Identifies browser failures in reports. Dotted so the issue formatter's
    /// short-name rule renders it as "Browser", giving titles of the form
    /// "[field error] TypeError in Browser".
    /// </summary>
    public const string BrowserSourceContext = "ComicMaintainer.Browser";
    private readonly IErrorReportFactory _reportFactory;
    private readonly IErrorReportQueue _queue;
    private readonly IOptionsMonitor<AppSettings> _appSettings;
    private readonly ILogger<ClientErrorsController> _logger;

    public ClientErrorsController(
        IErrorReportFactory reportFactory,
        IErrorReportQueue queue,
        IOptionsMonitor<AppSettings> appSettings,
        ILogger<ClientErrorsController> logger)
    {
        _reportFactory = reportFactory;
        _queue = queue;
        _appSettings = appSettings;
        _logger = logger;
    }

    /// <summary>
    /// Records one uncaught browser error.
    /// </summary>
    /// <remarks>
    /// Always answers 202 once the payload is well formed, whatever happens
    /// next. The browser has no use for the outcome and is in the middle of
    /// handling its own failure; telling it that reporting is switched off, or
    /// that the queue is full, would only invite it to retry.
    /// </remarks>
    [HttpPost]
    public ActionResult ReportClientError([FromBody] ClientErrorRequest request)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Request body is required" });
        }

        // Reporting is off by default and the destination is the project's own
        // repository, so an instance that never opted in must not even build a
        // report from browser-supplied text.
        if (!_appSettings.CurrentValue.ErrorReportingEnabled)
        {
            return Accepted();
        }

        var name = ClientErrorNormalizer.NormalizeName(request.Name);
        var message = ClientErrorNormalizer.NormalizeMessage(request.Message);
        var stack = ClientErrorNormalizer.NormalizeStack(request.Stack);
        var script = ClientErrorNormalizer.NormalizeLocation(request.Source);
        var page = ClientErrorNormalizer.NormalizeLocation(request.Url);
        var kind = request.Kind == ClientErrorKind.UnhandledRejection
            ? "unhandled promise rejection"
            : "uncaught error";

        // The template is what the fingerprint is computed from, so it carries
        // only the stable identity of the defect; the volatile detail lives in
        // the rendered message, exactly as it does for a server-side failure.
        var template = $"Browser {kind} on {page}: {ClientErrorNormalizer.NormalizeMessageTemplate(request.Message)}";

        var rendered =
            $"{name}: {message}{Environment.NewLine}" +
            $"kind: {kind}{Environment.NewLine}" +
            $"page: {page}{Environment.NewLine}" +
            $"script: {script}{Environment.NewLine}" +
            $"user agent: {ClientErrorNormalizer.NormalizeMessage(Request.Headers.UserAgent.ToString())}";

        var report = _reportFactory.CreateExternal(
            "Error",
            template,
            rendered,
            new ExternalFailure(name, message, stack),
            BrowserSourceContext,
            HttpContext.TraceIdentifier,
            DateTime.UtcNow);

        if (!_queue.TryEnqueue(report))
        {
            // Logged as a warning on purpose: an error here would be captured
            // by the reporting sink and queued behind the very backlog that
            // just rejected this report.
            _logger.LogWarning(
                "A browser error report was dropped because the report queue was full (fingerprint {Fingerprint})",
                report.Fingerprint);
        }

        return Accepted();
    }
}

/// <summary>Which browser hook caught the failure.</summary>
public enum ClientErrorKind
{
    /// <summary>A <c>window</c> 'error' event.</summary>
    Error = 0,

    /// <summary>A <c>window</c> 'unhandledrejection' event.</summary>
    UnhandledRejection = 1
}

/// <summary>
/// What the browser reports about an uncaught failure.
/// </summary>
/// <remarks>
/// Every field is bounded here as well as in
/// <see cref="ClientErrorNormalizer"/>: model binding rejects an oversized
/// payload before any of it is processed, while the normalizer guarantees the
/// bound regardless of how the report was constructed.
/// </remarks>
public class ClientErrorRequest
{
    /// <summary>The error's type name, e.g. <c>TypeError</c>.</summary>
    [MaxLength(ClientErrorNormalizer.MaxNameLength)]
    public string? Name { get; set; }

    /// <summary>The error's message.</summary>
    [MaxLength(ClientErrorNormalizer.MaxMessageLength)]
    public string? Message { get; set; }

    /// <summary>The error's stack, in whatever shape the engine produces.</summary>
    [MaxLength(ClientErrorNormalizer.MaxStackLength)]
    public string? Stack { get; set; }

    /// <summary>URL of the script that threw, when the engine reports one.</summary>
    [MaxLength(ClientErrorNormalizer.MaxUrlLength)]
    public string? Source { get; set; }

    /// <summary>URL of the page the failure happened on.</summary>
    [MaxLength(ClientErrorNormalizer.MaxUrlLength)]
    public string? Url { get; set; }

    /// <summary>Which browser hook caught it.</summary>
    public ClientErrorKind Kind { get; set; } = ClientErrorKind.Error;
}
