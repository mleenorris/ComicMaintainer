namespace ComicMaintainer.Core.Models;

/// <summary>
/// Supported delivery formats for emailing a comic to an ereader.
/// </summary>
public static class EmailDeliveryFormat
{
    /// <summary>Send the comic archive (CBZ/CBR) untouched.</summary>
    public const string Original = "original";

    /// <summary>Convert the comic to EPUB before sending.</summary>
    public const string Epub = "epub";

    /// <summary>Series subscriptions only: inherit the device's default format.</summary>
    public const string Device = "device";

    public static bool IsConcreteFormat(string? value) =>
        string.Equals(value, Original, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, Epub, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Normalizes a user-supplied format to <see cref="Original"/> or
    /// <see cref="Epub"/>, throwing when the value is not recognized.
    /// Null/empty falls back to <paramref name="fallback"/>.
    /// </summary>
    public static string NormalizeOrThrow(string? value, string fallback, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        // Return the constant rather than the caller's string so no user-controlled
        // value flows onward into logs or storage.
        var trimmed = value.Trim().ToLowerInvariant();
        if (trimmed == Original)
        {
            return Original;
        }

        if (trimmed == Epub)
        {
            return Epub;
        }

        throw new ArgumentException($"Unsupported delivery format '{value}'. Expected 'original' or 'epub'.", paramName);
    }

    /// <summary>
    /// Normalizes a subscription format, which may additionally be
    /// <see cref="Device"/> to inherit the device default.
    /// </summary>
    public static string NormalizeSubscriptionOrThrow(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Device;
        }

        var trimmed = value.Trim().ToLowerInvariant();
        if (trimmed == Device)
        {
            return Device;
        }

        if (trimmed == Original)
        {
            return Original;
        }

        if (trimmed == Epub)
        {
            return Epub;
        }

        throw new ArgumentException($"Unsupported delivery format '{value}'. Expected 'original', 'epub' or 'device'.", paramName);
    }
}

/// <summary>
/// How a batch of issues is condensed into EPUB books before delivery.
/// </summary>
public static class EmailCondenseMode
{
    /// <summary>One book per issue (the historical behaviour).</summary>
    public const string None = "none";

    /// <summary>A book for every N issues, in reading order.</summary>
    public const string Count = "count";

    /// <summary>A single book containing every selected issue.</summary>
    public const string All = "all";

    /// <summary>Upper bound on the issues that may be condensed into one book.</summary>
    public const int MaxIssuesPerBook = 500;

    public static bool IsCondensing(string? value) =>
        string.Equals(value, Count, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, All, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Normalizes a user-supplied condense mode. Null/empty means <see cref="None"/>.
    /// </summary>
    public static string NormalizeOrThrow(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return None;
        }

        // Return the constant rather than the caller's string so no user-controlled
        // value flows onward into logs or storage.
        var trimmed = value.Trim().ToLowerInvariant();
        return trimmed switch
        {
            None => None,
            Count => Count,
            All => All,
            _ => throw new ArgumentException(
                $"Unsupported condense mode '{value}'. Expected 'none', 'count' or 'all'.",
                paramName)
        };
    }

    /// <summary>
    /// Validates the issues-per-book value for <paramref name="mode"/>. Only
    /// <see cref="Count"/> uses it; the other modes ignore it.
    /// </summary>
    public static int NormalizeIssuesPerBookOrThrow(string mode, int? issuesPerBook, int totalIssues, string paramName)
    {
        if (!string.Equals(mode, Count, StringComparison.Ordinal))
        {
            return Math.Max(1, totalIssues);
        }

        if (issuesPerBook is not int value || value < 2)
        {
            throw new ArgumentException("Condensing by count requires at least 2 issues per book.", paramName);
        }

        if (value > MaxIssuesPerBook)
        {
            throw new ArgumentException(
                $"At most {MaxIssuesPerBook} issues can be condensed into a single book.",
                paramName);
        }

        return value;
    }
}

/// <summary>Status values used by <c>ComicEmailDeliveryEntity.Status</c>.</summary>
public static class EmailDeliveryStatus
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Failed = "failed";
}

/// <summary>Origin of a delivery: user-initiated or series auto-send.</summary>
public static class EmailDeliverySource
{
    public const string Manual = "manual";
    public const string Auto = "auto";
}

/// <summary>A saved ereader destination.</summary>
public record EreaderDeviceDto(
    int Id,
    string Name,
    string EmailAddress,
    string DeliveryFormat,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>An automatic-delivery subscription for a series.</summary>
public record SeriesEmailSubscriptionDto(
    int Id,
    string NormalizedSeriesKey,
    string SeriesTitle,
    int DeviceId,
    string DeviceName,
    string DeviceEmail,
    string DeliveryFormat,
    bool Enabled,
    DateTime? LastSentAt);

/// <summary>A single (attempted) comic email.</summary>
public record ComicEmailDeliveryDto(
    int Id,
    string FilePath,
    string FileName,
    int DeviceId,
    string DeviceName,
    string DeviceEmail,
    string DeliveryFormat,
    string Status,
    string Source,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime? SentAt,
    int IssueCount = 1);

/// <summary>
/// One condensed book: the issues it contains, the name it will be delivered
/// under and how large the generated EPUB is expected to be.
/// </summary>
/// <param name="EstimatedBytes">
/// Best-effort size of the generated EPUB. The pages are carried over with
/// their original compression, so the sum of the source archives plus a small
/// container overhead is a close approximation.
/// </param>
/// <param name="ExceedsAttachmentLimit">
/// True when <paramref name="EstimatedBytes"/> is over the configured mail
/// attachment limit. Such a book should be downloaded rather than emailed:
/// the converter will still try to recompress it, but heavily condensed books
/// cannot realistically be shrunk far enough.
/// </param>
public record CondensedBookDto(
    string DisplayName,
    IReadOnlyList<string> Files,
    long EstimatedBytes,
    bool ExceedsAttachmentLimit);

/// <summary>
/// What condensing a selection would produce, so the user can be warned before
/// a mass condense that cannot be emailed.
/// </summary>
public record CondensePlanDto(
    string Mode,
    int TotalIssues,
    int IssuesPerBook,
    long MaxAttachmentBytes,
    IReadOnlyList<CondensedBookDto> Books,
    IReadOnlyDictionary<string, string> Skipped)
{
    /// <summary>Books that are too large to email and must be downloaded instead.</summary>
    public int OversizedBookCount => Books.Count(b => b.ExceedsAttachmentLimit);

    /// <summary>True when every book fits the mail attachment limit.</summary>
    public bool CanEmail => Books.Count > 0 && OversizedBookCount == 0;
}

/// <summary>
/// A condensed book generated for download instead of email. The file lives in
/// a temporary directory the caller is responsible for deleting once streamed.
/// </summary>
public record CondensedBookFile(string FilePath, string FileName);

/// <summary>
/// Outcome of queueing a batch of files for delivery: the deliveries that were
/// created plus the files that were skipped (with the reason).
/// </summary>
public record EmailQueueResult(
    IReadOnlyList<ComicEmailDeliveryDto> Queued,
    IReadOnlyDictionary<string, string> Skipped);
