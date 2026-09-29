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
/// point. The preview surface shows both the full stored report and the exact
/// shortened body this transport puts in the URL, so the user consents to what
/// GitHub actually receives rather than to a longer version of it.
/// </para>
/// <para>
/// GitHub's query-string prefill is subject to a URL length limit (roughly 8 KB
/// in practice, lower in some browsers). The body is therefore built to a
/// bounded, structurally complete form of at most
/// <see cref="MaxPrefilledBodyLength"/> characters — whole sections in priority
/// order rather than a character-offset cut that could land inside a code fence
/// — and the administrator is shown that exact shortened body before opening
/// the link.
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
    /// Builds the exact issue body that the prefill URL will carry. Exposed so
    /// the administrator can be shown what will actually be submitted rather
    /// than the unbounded body.
    /// </summary>
    public static string BuildPrefilledBody(ErrorReport report) =>
        ErrorReportIssueBuilder.BuildBody(report, MaxPrefilledBodyLength);

    /// <summary>
    /// Builds the pre-filled issue URL for <paramref name="report"/>.
    /// </summary>
    public static string BuildIssueUrl(string repository, ErrorReport report)
    {
        var body = BuildPrefilledBody(report);
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
