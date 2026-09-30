using System.Globalization;
using System.Text;
using ComicMaintainer.Core.Data;

namespace ComicMaintainer.Core.ErrorReporting;

/// <summary>
/// Renders an <see cref="ErrorReport"/> into the GitHub issue title and body.
/// </summary>
/// <remarks>
/// The body is written for two audiences at once: a human triaging the tracker,
/// and a coding agent that will be assigned the issue and has no other context.
/// That is why the source context, the stack and the fingerprint are all
/// explicit — the agent needs to find the code from the issue alone.
/// </remarks>
public static class ErrorReportIssueFormatter
{
    /// <summary>Applied to every automatically filed issue.</summary>
    public const string FieldErrorLabel = "field-error";

    /// <summary>Marks the issue as machine-filed so it can be swept or filtered.</summary>
    public const string AutomatedLabel = "automated";

    /// <summary>
    /// Fenced block that carries the fingerprint in a machine-readable form.
    /// Lets a future ingest (or a workflow closing the issue) match an issue
    /// back to its fingerprint without parsing prose.
    /// </summary>
    public const string FingerprintBlockLanguage = "comicmaintainer-error-report";

    public static string BuildTitle(ErrorReport report)
    {
        var what = string.IsNullOrWhiteSpace(report.ExceptionType)
            ? "Error"
            : ShortTypeName(report.ExceptionType);

        var where = string.IsNullOrWhiteSpace(report.SourceContext)
            ? "unknown source"
            : ShortTypeName(report.SourceContext);

        // GitHub truncates long titles in list views; keep it scannable.
        return ErrorReportRedactor.Truncate($"[field error] {what} in {where}", 180);
    }

    public static IReadOnlyList<string> BuildLabels(ErrorReport report)
    {
        var severity = string.Equals(report.Level, "Fatal", StringComparison.OrdinalIgnoreCase)
            ? "severity:fatal"
            : "severity:error";

        return new[] { FieldErrorLabel, AutomatedLabel, severity };
    }

    public static string BuildBody(ErrorReport report, ErrorReportEntity? existing = null)
    {
        var occurrences = existing?.OccurrenceCount ?? 1;
        var builder = new StringBuilder();

        builder.AppendLine("An error was reported automatically from a running ComicMaintainer instance.");
        builder.AppendLine();
        builder.AppendLine("| | |");
        builder.AppendLine("|---|---|");
        AppendRow(builder, "Level", report.Level);
        AppendRow(builder, "Exception", report.ExceptionType ?? "(none)");
        AppendRow(builder, "Source", report.SourceContext ?? "(unknown)");
        AppendRow(builder, "App version", report.AppVersion ?? "(unknown)");
        AppendRow(builder, "Runtime", report.RuntimeVersion ?? "(unknown)");
        AppendRow(builder, "OS", report.OperatingSystem ?? "(unknown)");
        AppendRow(builder, "First seen (UTC)", (existing?.FirstSeenAt ?? report.TimestampUtc).ToString("u", CultureInfo.InvariantCulture));
        AppendRow(builder, "Occurrences", occurrences.ToString(CultureInfo.InvariantCulture));
        AppendRow(builder, "Correlation id", report.CorrelationId ?? "(none)");
        builder.AppendLine();

        builder.AppendLine("### Message");
        builder.AppendLine();
        builder.AppendLine("```text");
        builder.AppendLine(Fence(report.RenderedMessage));
        builder.AppendLine("```");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(report.MessageTemplate)
            && !string.Equals(report.MessageTemplate, report.RenderedMessage, StringComparison.Ordinal))
        {
            builder.AppendLine("### Message template");
            builder.AppendLine();
            builder.AppendLine("```text");
            builder.AppendLine(Fence(report.MessageTemplate));
            builder.AppendLine("```");
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(report.ExceptionMessage))
        {
            builder.AppendLine("### Exception");
            builder.AppendLine();
            builder.AppendLine("```text");
            builder.AppendLine(Fence(report.ExceptionMessage));
            builder.AppendLine("```");
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(report.StackTrace))
        {
            builder.AppendLine("### Stack trace");
            builder.AppendLine();
            builder.AppendLine("```text");
            builder.AppendLine(Fence(report.StackTrace));
            builder.AppendLine("```");
            builder.AppendLine();
        }

        builder.AppendLine("### Notes");
        builder.AppendLine();
        builder.AppendLine("- Paths, email addresses and credentials are redacted before the report leaves the reporting instance, so some detail is intentionally missing.");
        builder.AppendLine("- Occurrence counts are per-installation; the same defect on another instance files its own issue.");
        builder.AppendLine();

        builder.AppendLine(BuildFingerprintBlock(report.Fingerprint));

        return builder.ToString();
    }

    /// <summary>Body used when an already-reported failure happens again.</summary>
    public static string BuildRecurrenceComment(ErrorReport report, ErrorReportEntity existing)
    {
        var builder = new StringBuilder();
        builder.Append("Still occurring: ")
            .Append(existing.OccurrenceCount.ToString(CultureInfo.InvariantCulture))
            .Append(" occurrence(s) as of ")
            .Append(report.TimestampUtc.ToString("u", CultureInfo.InvariantCulture))
            .AppendLine(".");
        builder.AppendLine();
        AppendRow(builder, "App version", report.AppVersion ?? "(unknown)");
        AppendRow(builder, "Correlation id", report.CorrelationId ?? "(none)");
        builder.AppendLine();
        builder.AppendLine(BuildFingerprintBlock(report.Fingerprint));
        return builder.ToString();
    }

    public static string BuildFingerprintBlock(string fingerprint)
        => $"```{FingerprintBlockLanguage}\nfingerprint: {fingerprint}\n```";

    private static void AppendRow(StringBuilder builder, string key, string value)
        => builder.Append("| ").Append(key).Append(" | ").Append(EscapeCell(value)).AppendLine(" |");

    /// <summary>Keeps a value from breaking out of a Markdown table cell.</summary>
    private static string EscapeCell(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>
    /// Stops report text that happens to contain a triple backtick from
    /// terminating the fence and injecting Markdown (or an HTML comment that
    /// hides the rest of the issue) into the body.
    /// </summary>
    private static string Fence(string? value)
        => (value ?? string.Empty).Replace("```", "``\u200b`", StringComparison.Ordinal);

    private static string ShortTypeName(string fullName)
    {
        var lastDot = fullName.LastIndexOf('.');
        return lastDot >= 0 && lastDot < fullName.Length - 1
            ? fullName[(lastDot + 1)..]
            : fullName;
    }
}
