using System.Reflection;
using System.Runtime.InteropServices;
using ComicMaintainer.Core.Configuration;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.ErrorReporting;

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
    {
        var secrets = CollectLiteralSecrets();

        // The template is redacted too: templates are developer-authored and
        // should be safe, but a template built by string interpolation at a
        // call site would otherwise slip an interpolated path straight through.
        var redactedTemplate = ErrorReportRedactor.Redact(messageTemplate, secrets, 2048) ?? string.Empty;
        var redactedMessage = ErrorReportRedactor.Redact(renderedMessage, secrets) ?? string.Empty;
        var stackTrace = ErrorReportRedactor.RedactStackTrace(exception?.ToString(), secrets, maxLength: 8000);

        var fingerprint = ErrorReportFingerprint.Compute(
            exception?.GetType().FullName,
            exception?.StackTrace,
            messageTemplate,
            sourceContext);

        return new ErrorReport
        {
            Fingerprint = fingerprint,
            Level = level,
            MessageTemplate = redactedTemplate,
            RenderedMessage = redactedMessage,
            ExceptionType = exception?.GetType().FullName,
            ExceptionMessage = ErrorReportRedactor.Redact(exception?.Message, secrets, 2048),
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
