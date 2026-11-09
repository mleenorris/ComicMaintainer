namespace ComicMaintainer.Core.Configuration;

/// <summary>
/// One save of the Settings page (see <c>ISettingsService.UpdateSettingsAsync</c>). Every group
/// and field is optional: null keeps the current value, so a client leaves out what it does
/// not change, and a text field sent as an empty string is cleared. Base URLs are the
/// exception: a blank one also keeps the stored URL.
/// </summary>
public sealed record SettingsUpdate
{
    /// <summary><see cref="AppSettings.FilenameFormat"/>; must not be blank.</summary>
    public string? FilenameFormat { get; init; }

    /// <summary><see cref="AppSettings.LogMaxBytes"/>; greater than 0.</summary>
    public int? LogMaxBytes { get; init; }

    /// <summary><see cref="AppSettings.IssueNumberPadding"/>; 0 to <see cref="AppSettings.MaxIssueNumberPadding"/>.</summary>
    public int? IssueNumberPadding { get; init; }

    public DatabaseCleanupUpdate? DatabaseCleanup { get; init; }
    public ExternalSeriesMetadataUpdate? ExternalSeriesMetadata { get; init; }
    public SeriesImageLimitsUpdate? SeriesImageLimits { get; init; }
    public EmailUpdate? Email { get; init; }
    public EmailRateLimitUpdate? EmailRateLimit { get; init; }
    public SuwayomiUpdate? Suwayomi { get; init; }
    public ErrorReportingUpdate? ErrorReporting { get; init; }
    public NotificationsUpdate? Notifications { get; init; }
}

/// <summary>The database-cleanup group of a <see cref="SettingsUpdate"/>; see <see cref="DatabaseCleanupSettings"/> for the ranges.</summary>
public sealed record DatabaseCleanupUpdate(
    int? IntervalHours = null,
    int? StaleCleanupMaxRemovalPercent = null,
    int? StaleCleanupMissingGraceHours = null,
    string? StaleCleanupSentinelFile = null,
    int? ProcessingHistoryRetentionDays = null,
    int? ProcessingHistoryMaxEntries = null);

/// <summary>The external series metadata group of a <see cref="SettingsUpdate"/>.</summary>
public sealed record ExternalSeriesMetadataUpdate(
    bool? Enabled = null,
    string? ComicVineApiKey = null,
    string? ComicVineBaseUrl = null,
    bool? EnableMangaDex = null,
    string? MangaDexBaseUrl = null,
    bool? EnableAniList = null,
    string? AniListBaseUrl = null);

/// <summary>The series cover image limits of a <see cref="SettingsUpdate"/>, in bytes and pixels.</summary>
public sealed record SeriesImageLimitsUpdate(
    int? MaxBytes = null,
    int? MaxDownloadBytes = null,
    int? MaxDimension = null);

/// <summary>The SMTP group of a <see cref="SettingsUpdate"/>.</summary>
public sealed record EmailUpdate(
    string? SmtpHost = null,
    int? SmtpPort = null,
    string? SmtpUsername = null,
    string? SmtpPassword = null,
    bool? SmtpUseSsl = null,
    bool? SmtpAllowInsecure = null,
    string? EmailFromAddress = null,
    string? EmailFromName = null,
    int? EmailMaxAttachmentMb = null);

/// <summary>The email rate limit of a <see cref="SettingsUpdate"/>; 0 disables a limit.</summary>
public sealed record EmailRateLimitUpdate(
    int? MaxSendsPerHour = null,
    int? MaxSendsPerDay = null,
    int? MinSecondsBetweenSends = null);

/// <summary>The Suwayomi download-server group of a <see cref="SettingsUpdate"/>.</summary>
public sealed record SuwayomiUpdate(
    bool? Enabled = null,
    string? BaseUrl = null,
    string? Username = null,
    string? Password = null,
    string? DownloadsDirectory = null,
    string? ImportDirectory = null);

/// <summary>The notifications group of a <see cref="SettingsUpdate"/>: one entry per target.</summary>
public sealed record NotificationsUpdate(
    NotificationTargetUpdate? Ntfy = null,
    NotificationTargetUpdate? Gotify = null,
    NotificationTargetUpdate? Discord = null,
    NotificationTargetUpdate? Webhook = null,
    NotificationTargetUpdate? Komga = null,
    NotificationTargetUpdate? Kavita = null);

/// <summary>
/// One notification target. Which fields apply depends on the target; the others are ignored.
/// <see cref="Url"/> is the server's base URL, except for Discord and the webhook, where it is
/// the secret webhook URL: null keeps it and an empty string clears it, as for
/// <see cref="Token"/>, which holds the target's token or API key.
/// </summary>
/// <param name="Enabled">Whether the target is used.</param>
/// <param name="Url">The target's URL.</param>
/// <param name="Token">The target's token or API key.</param>
/// <param name="Topic">The ntfy topic.</param>
/// <param name="LibraryId">The library a rescan target scans; every library when empty.</param>
/// <param name="Events">The comma-separated events the target receives; empty for none.</param>
public sealed record NotificationTargetUpdate(
    bool? Enabled = null,
    string? Url = null,
    string? Token = null,
    string? Topic = null,
    string? LibraryId = null,
    string? Events = null);

/// <summary>The automated error-reporting group of a <see cref="SettingsUpdate"/>.</summary>
public sealed record ErrorReportingUpdate(
    bool? Enabled = null,
    string? GitHubToken = null,
    string? Assignee = null,
    int? MaxIssuesPerDay = null,
    int? DedupeWindowHours = null,
    bool? CommentOnRecurrence = null);
