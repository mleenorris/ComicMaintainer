using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.ErrorReporting.Interfaces;
using ComicMaintainer.Core.ErrorReporting.Models;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.ErrorReporting.Services;

/// <summary>
/// Default transport. Transmits nothing: it builds a pre-filled
/// <c>issues/new</c> URL that the user reviews and submits themselves.
/// </summary>
/// <remarks>
/// <para>
/// This is the only mode that works for every self-hosted install, because it
/// needs no credential and gives the owner an explicit, per-report consent
/// point. The report body is identical to the one the automatic transport would
/// post, so what the user sees in the preview is exactly what GitHub receives.
/// </para>
/// <para>
/// GitHub's query-string prefill is subject to a URL length limit (roughly 8 KB
/// in practice, lower in some browsers). The body is therefore truncated to
/// <see cref="MaxPrefilledBodyLength"/> before encoding, with a note telling the
/// user the full text is available in the app.
/// </para>
/// </remarks>
public sealed class ConsentUrlErrorReportTransport : IErrorReportTransport
{
    /// <summary>
    /// Characters of issue body embedded in the prefill URL. Kept well under
    /// the practical URL limit because percent-encoding can triple the length
    /// of a stack trace.
    /// </summary>
    public const int MaxPrefilledBodyLength = 2000;

    private readonly IOptionsMonitor<AppSettings> _settings;

    public ConsentUrlErrorReportTransport(IOptionsMonitor<AppSettings> settings)
    {
        _settings = settings;
    }

    /// <inheritdoc />
    public string Mode => "manual";

    /// <inheritdoc />
    public bool TransmitsAutomatically => false;

    /// <inheritdoc />
    public Task<ErrorReportTransportResult> SendAsync(
        ErrorReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        var repository = NormalizeRepository(_settings.CurrentValue.GitHubRepository);
        var url = BuildIssueUrl(repository, report);

        return Task.FromResult(new ErrorReportTransportResult(
            Delivered: false,
            IssueNumber: null,
            IssueUrl: url,
            Detail: "Review the pre-filled issue and submit it on GitHub to send this report."));
    }

    /// <summary>
    /// Builds the pre-filled issue URL for <paramref name="report"/>.
    /// </summary>
    public static string BuildIssueUrl(string repository, ErrorReport report)
    {
        var body = ErrorReportIssueBuilder.BuildBody(report);
        if (body.Length > MaxPrefilledBodyLength)
        {
            body = body[..MaxPrefilledBodyLength]
                   + "\n\n_(truncated — open Settings → Error reporting on your instance for the full report)_\n";
        }

        var query = string.Join("&",
            "template=auto_error_report.yml",
            "labels=" + Uri.EscapeDataString(string.Join(",", ErrorReportIssueBuilder.BuildLabels(report))),
            "title=" + Uri.EscapeDataString(ErrorReportIssueBuilder.BuildTitle(report)),
            "report=" + Uri.EscapeDataString(body));

        return $"https://github.com/{repository}/issues/new?{query}";
    }

    /// <summary>
    /// Reduces the configured value to a bare <c>owner/repo</c>. A malformed
    /// setting must not be able to redirect the user to an arbitrary host, so
    /// anything unrecognised falls back to the upstream repository.
    /// </summary>
    internal static string NormalizeRepository(string? configured)
    {
        const string fallback = "mleenorris/ComicMaintainer";

        if (string.IsNullOrWhiteSpace(configured))
        {
            return fallback;
        }

        var value = configured.Trim().Trim('/');

        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return fallback;
        }

        return IsSegmentSafe(parts[0]) && IsSegmentSafe(parts[1])
            ? $"{parts[0]}/{parts[1]}"
            : fallback;
    }

    private static bool IsSegmentSafe(string segment) =>
        segment.Length is > 0 and <= 100
        && segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
