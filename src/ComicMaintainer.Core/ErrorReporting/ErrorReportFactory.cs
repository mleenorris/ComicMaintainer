using System.Reflection;
using System.Runtime.InteropServices;
using ComicMaintainer.Core.Configuration;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.ErrorReporting;

/// <summary>
/// A failure that did not arrive as a .NET <see cref="Exception"/> and therefore
/// has to supply its own type, message and stack — currently an uncaught error
/// raised by the browser.
/// </summary>
/// <param name="Type">
/// The failing error's type name, e.g. <c>SyntaxError</c>. Used verbatim as the
/// report's exception type, so it ends up in the issue title.
/// </param>
/// <param name="Message">The error's message.</param>
/// <param name="StackTrace">
/// The stack, already normalized into the <c>at member (location)</c> frame
/// shape <see cref="ErrorReportFingerprint"/> understands.
/// </param>
public readonly record struct ExternalFailure(string? Type, string? Message, string? StackTrace);

/// <summary>
/// Builds redacted, fingerprinted <see cref="ErrorReport"/> instances.
/// </summary>
/// <remarks>
/// Centralised so that there is exactly one place where raw failure text is
/// turned into a shareable report. Any future capture path (a client-side error
/// endpoint, a test report from the settings page) must go through here rather
/// than constructing an <see cref="ErrorReport"/> directly, or it will bypass
/// redaction.
/// </remarks>
public interface IErrorReportFactory
{
    ErrorReport Create(
        string level,
        string? messageTemplate,
        string? renderedMessage,
        Exception? exception,
        string? sourceContext,
        string? correlationId,
        DateTime timestampUtc);

    /// <summary>
    /// Builds a report for a failure captured outside the .NET exception model.
    /// Identical to <see cref="Create"/> in every other respect: the same
    /// redaction runs over every string and the fingerprint is computed from
    /// the same unredacted inputs, so a browser defect dedupes exactly like a
    /// server one.
    /// </summary>
    ErrorReport CreateExternal(
        string level,
        string? messageTemplate,
        string? renderedMessage,
        ExternalFailure failure,
        string? sourceContext,
        string? correlationId,
        DateTime timestampUtc);
}

/// <inheritdoc />
public sealed class ErrorReportFactory : IErrorReportFactory
{
    private readonly IOptionsMonitor<AppSettings> _appSettings;
    private readonly string _appVersion;

    public ErrorReportFactory(IOptionsMonitor<AppSettings> appSettings)
    {
        _appSettings = appSettings;
        _appVersion = ResolveAppVersion();
    }

    public ErrorReport Create(
        string level,
        string? messageTemplate,
        string? renderedMessage,
        Exception? exception,
        string? sourceContext,
        string? correlationId,
        DateTime timestampUtc)
        => Build(
            level,
            messageTemplate,
            renderedMessage,
            exception?.GetType().FullName,
            exception?.Message,
            exception?.StackTrace,
            // ToString() carries the type and message above the frames, which is
            // what makes a reported stack readable on its own.
            exception?.ToString(),
            sourceContext,
            correlationId,
            timestampUtc);

    public ErrorReport CreateExternal(
        string level,
        string? messageTemplate,
        string? renderedMessage,
        ExternalFailure failure,
        string? sourceContext,
        string? correlationId,
        DateTime timestampUtc)
        => Build(
            level,
            messageTemplate,
            renderedMessage,
            string.IsNullOrWhiteSpace(failure.Type) ? null : failure.Type,
            failure.Message,
            failure.StackTrace,
            ComposeExternalStackTrace(failure),
            sourceContext,
            correlationId,
            timestampUtc);

    private ErrorReport Build(
        string level,
        string? messageTemplate,
        string? renderedMessage,
        string? failureType,
        string? failureMessage,
        string? fingerprintStackTrace,
        string? reportedStackTrace,
        string? sourceContext,
        string? correlationId,
        DateTime timestampUtc)
    {
        var secrets = CollectLiteralSecrets();

        // The template is redacted too: templates are developer-authored and
        // should be safe, but a template built by string interpolation at a
        // call site would otherwise slip an interpolated path straight through.
        var redactedTemplate = ErrorReportRedactor.Redact(messageTemplate, secrets, 2048) ?? string.Empty;
        var redactedMessage = ErrorReportRedactor.Redact(renderedMessage, secrets) ?? string.Empty;
        var stackTrace = ErrorReportRedactor.RedactStackTrace(reportedStackTrace, secrets, maxLength: 8000);

        // Deliberately fingerprinted from the *unredacted* inputs: redaction is
        // lossy and input-dependent, so folding it in would let two occurrences
        // of one defect fingerprint differently.
        var fingerprint = ErrorReportFingerprint.Compute(
            failureType,
            fingerprintStackTrace,
            messageTemplate,
            sourceContext);

        return new ErrorReport
        {
            Fingerprint = fingerprint,
            Level = level,
            MessageTemplate = redactedTemplate,
            RenderedMessage = redactedMessage,
            ExceptionType = failureType,
            ExceptionMessage = ErrorReportRedactor.Redact(failureMessage, secrets, 2048),
            StackTrace = stackTrace,
            SourceContext = sourceContext,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) || correlationId == "-" ? null : correlationId,
            AppVersion = _appVersion,
            OperatingSystem = RuntimeInformation.OSDescription,
            RuntimeVersion = RuntimeInformation.FrameworkDescription,
            TimestampUtc = timestampUtc
        };
    }

    /// <summary>
    /// Mirrors <see cref="Exception.ToString"/> for an external failure so the
    /// reported stack reads the same way as a .NET one.
    /// </summary>
    private static string? ComposeExternalStackTrace(ExternalFailure failure)
    {
        var header = string.IsNullOrWhiteSpace(failure.Type)
            ? failure.Message?.Trim()
            : string.IsNullOrWhiteSpace(failure.Message)
                ? failure.Type.Trim()
                : $"{failure.Type.Trim()}: {failure.Message.Trim()}";

        var frames = failure.StackTrace?.Trim();

        if (string.IsNullOrEmpty(header))
        {
            return string.IsNullOrEmpty(frames) ? null : frames;
        }

        return string.IsNullOrEmpty(frames) ? header : $"{header}\n{frames}";
    }

    /// <summary>
    /// Every configured secret value, so that a secret echoed verbatim by a
    /// third-party library (an SMTP client quoting the credential it was
    /// handed) is masked even when it appears with no key name beside it.
    /// </summary>
    private IReadOnlyCollection<string> CollectLiteralSecrets()
    {
        var settings = _appSettings.CurrentValue;
        var secrets = new List<string>(3);

        AddIfPresent(secrets, settings.SmtpPassword);
        AddIfPresent(secrets, settings.ComicVineApiKey);
        AddIfPresent(secrets, settings.ErrorReportingGitHubToken);

        return secrets;
    }

    private static void AddIfPresent(List<string> secrets, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            secrets.Add(value);
        }
    }

    private static string ResolveAppVersion()
        => Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";
}
