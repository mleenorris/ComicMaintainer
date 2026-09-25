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

        var trimmed = value.Trim().ToLowerInvariant();
        if (trimmed is Original or Epub)
        {
            return trimmed;
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
        if (trimmed is Original or Epub or Device)
        {
            return trimmed;
        }

        throw new ArgumentException($"Unsupported delivery format '{value}'. Expected 'original', 'epub' or 'device'.", paramName);
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
    DateTime? SentAt);

/// <summary>
/// Outcome of queueing a batch of files for delivery: the deliveries that were
/// created plus the files that were skipped (with the reason).
/// </summary>
public record EmailQueueResult(
    IReadOnlyList<ComicEmailDeliveryDto> Queued,
    IReadOnlyDictionary<string, string> Skipped);
