using System.Linq.Expressions;
using System.Reflection;
using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Configuration;

/// <summary>
/// One environment variable that overrides an <see cref="AppSettings"/> property. The same
/// parsing decides both whether the variable is applied and whether the setting counts as
/// pinned: a variable that is empty or holds an unusable value does neither, so the value
/// saved in the Settings page stays in effect.
/// </summary>
public sealed class EnvironmentOverride
{
    private readonly PropertyInfo _property;
    private readonly Func<string, object?> _parse;

    internal EnvironmentOverride(string variable, PropertyInfo property, string? apiField, Func<string, object?> parse)
    {
        Variable = variable;
        _property = property;
        ApiField = apiField;
        _parse = parse;
    }

    /// <summary>The environment variable's name.</summary>
    public string Variable { get; }

    /// <summary>The <see cref="AppSettings"/> property, which is also its key in user-settings.json.</summary>
    public string SettingName => _property.Name;

    /// <summary>
    /// The setting's key in <c>GET /api/settings</c>, or null when the Settings page has no field for it.
    /// </summary>
    public string? ApiField { get; }

    /// <summary>The value the variable currently forces; false when it does not force one.</summary>
    public bool TryGetForcedValue(out object? value)
    {
        var raw = Environment.GetEnvironmentVariable(Variable);
        value = string.IsNullOrEmpty(raw) ? null : _parse(raw);
        return value is not null;
    }

    /// <summary>Applies the variable to <paramref name="options"/> when it forces a value.</summary>
    public void ApplyTo(AppSettings options)
    {
        if (TryGetForcedValue(out var value))
        {
            _property.SetValue(options, value);
        }
    }
}

/// <summary>
/// Every environment variable that overrides a setting. This one table drives the overrides
/// themselves, the <c>env_locked</c> list in <c>GET /api/settings</c> and the refusal to save
/// a setting the environment pins.
/// </summary>
public static class AppSettingsEnvironmentOverrides
{
    /// <summary>
    /// Every override, in application order. CONFIG_DIR is not here: Program resolves it and
    /// publishes the directory it actually uses as AppSettings:ConfigDirectory, so the options,
    /// the database and the DataProtection keys always agree.
    /// </summary>
    public static IReadOnlyList<EnvironmentOverride> All { get; } =
    [
        Text("WATCHED_DIR", o => o.WatchedDirectory),
        Text("DUPLICATE_DIR", o => o.DuplicateDirectory),

        // Stale-entry cleanup guards. The sentinel is most useful from the environment: it
        // names a file on the library share that only exists while the share is mounted.
        Whole("STALE_CLEANUP_MAX_REMOVAL_PERCENT", o => o.StaleCleanupMaxRemovalPercent, 0, AppSettings.MaxStaleCleanupRemovalPercent, "stale_cleanup_max_removal_percent"),
        Whole("STALE_CLEANUP_MISSING_GRACE_HOURS", o => o.StaleCleanupMissingGraceHours, 0, AppSettings.MaxStaleCleanupMissingGraceHours, "stale_cleanup_missing_grace_hours"),
        Text("STALE_CLEANUP_SENTINEL_FILE", o => o.StaleCleanupSentinelFile, "stale_cleanup_sentinel_file"),
        Whole("PROCESSING_HISTORY_RETENTION_DAYS", o => o.ProcessingHistoryRetentionDays, 0, AppSettings.MaxProcessingHistoryRetentionDays, "processing_history_retention_days"),
        Whole("PROCESSING_HISTORY_MAX_ENTRIES", o => o.ProcessingHistoryMaxEntries, 0, AppSettings.MaxProcessingHistoryEntries, "processing_history_max_entries"),

        Text("BASE_PATH", o => o.BasePath),

        Flag("ENABLE_EXTERNAL_SERIES_METADATA", o => o.EnableExternalSeriesMetadata, "enable_external_series_metadata"),
        Text("COMICVINE_API_KEY", o => o.ComicVineApiKey, "comicvine_api_key_set"),
        Text("COMICVINE_BASE_URL", o => o.ComicVineBaseUrl, "comicvine_base_url"),
        Flag("ENABLE_MANGADEX_METADATA", o => o.EnableMangaDexMetadata, "enable_mangadex_metadata"),
        Text("MANGADEX_BASE_URL", o => o.MangaDexBaseUrl, "mangadex_base_url"),
        Flag("ENABLE_ANILIST_METADATA", o => o.EnableAniListMetadata, "enable_anilist_metadata"),
        Text("ANILIST_BASE_URL", o => o.AniListBaseUrl, "anilist_base_url"),

        Flag("ENABLE_SUWAYOMI", o => o.EnableSuwayomi, "enable_suwayomi"),
        Text("SUWAYOMI_BASE_URL", o => o.SuwayomiBaseUrl, "suwayomi_base_url"),
        Text("SUWAYOMI_USERNAME", o => o.SuwayomiUsername, "suwayomi_username"),
        Text("SUWAYOMI_PASSWORD", o => o.SuwayomiPassword, "suwayomi_password_set"),
        Text("SUWAYOMI_DOWNLOADS_DIR", o => o.SuwayomiDownloadsDirectory, "suwayomi_downloads_directory"),
        Text("SUWAYOMI_IMPORT_DIR", o => o.SuwayomiImportDirectory, "suwayomi_import_directory"),

        Whole("MANGADEX_REQUESTS_PER_SECOND", o => o.MangaDexRequestsPerSecond, 1, int.MaxValue),
        Whole("ANILIST_REQUESTS_PER_MINUTE", o => o.AniListRequestsPerMinute, 1, int.MaxValue),
        Whole("COMICVINE_REQUESTS_PER_HOUR", o => o.ComicVineRequestsPerHour, 1, int.MaxValue),
        Whole("METADATA_PROVIDER_TIMEOUT_SECONDS", o => o.MetadataProviderTimeoutSeconds, 1, int.MaxValue),

        Flag("FILL_COMICINFO_FROM_PROVIDERS", o => o.FillComicInfoFromProviders, "fill_comicinfo_from_providers"),
        Flag("DOWNLOAD_EXTERNAL_SERIES_IMAGES", o => o.DownloadExternalSeriesImages, "download_external_series_images"),
        Text("SERIES_IMAGE_CACHE_DIR", o => o.SeriesImageCacheDirectory),
        Whole("SERIES_IMAGE_MAX_BYTES", o => o.SeriesImageMaxBytes, 1, int.MaxValue, "series_image_max_bytes"),
        Whole("SERIES_IMAGE_MAX_DOWNLOAD_BYTES", o => o.SeriesImageMaxDownloadBytes, 1, int.MaxValue, "series_image_max_download_bytes"),
        Whole("SERIES_IMAGE_MAX_DIMENSION", o => o.SeriesImageMaxDimension, 1, int.MaxValue, "series_image_max_dimension"),

        Text("SMTP_HOST", o => o.SmtpHost, "smtp_host"),
        Whole("SMTP_PORT", o => o.SmtpPort, 1, 65535, "smtp_port"),
        Text("SMTP_USERNAME", o => o.SmtpUsername, "smtp_username"),
        Text("SMTP_PASSWORD", o => o.SmtpPassword, "smtp_password_set"),
        Flag("SMTP_USE_SSL", o => o.SmtpUseSsl, "smtp_use_ssl"),
        Flag("SMTP_ALLOW_INSECURE", o => o.SmtpAllowInsecure, "smtp_allow_insecure"),
        Text("EMAIL_FROM_ADDRESS", o => o.EmailFromAddress, "email_from_address"),
        Text("EMAIL_FROM_NAME", o => o.EmailFromName, "email_from_name"),
        Whole("EMAIL_MAX_ATTACHMENT_MB", o => o.EmailMaxAttachmentMegabytes, 1, int.MaxValue, "email_max_attachment_mb"),

        // Email rate limit. 0 is accepted so an environment variable can switch off a limit
        // saved from the UI.
        Whole("EMAIL_MAX_SENDS_PER_HOUR", o => o.EmailMaxSendsPerHour, 0, EmailRateLimitBounds.MaxSendsPerHour, "email_max_sends_per_hour"),
        Whole("EMAIL_MAX_SENDS_PER_DAY", o => o.EmailMaxSendsPerDay, 0, EmailRateLimitBounds.MaxSendsPerDay, "email_max_sends_per_day"),
        Whole("EMAIL_MIN_SECONDS_BETWEEN_SENDS", o => o.EmailMinSecondsBetweenSends, 0, EmailRateLimitBounds.MaxMinSecondsBetweenSends, "email_min_seconds_between_sends"),

        // Automated error reporting. Provided as environment variables as well as settings so
        // the token can be supplied by a Docker secret / orchestrator and never has to be typed
        // into (or stored in) user-settings.json.
        Flag("ERROR_REPORTING_ENABLED", o => o.ErrorReportingEnabled, "error_reporting_enabled"),
        Text("ERROR_REPORTING_GITHUB_TOKEN", o => o.ErrorReportingGitHubToken, "error_reporting_github_token_set"),
        Text("ERROR_REPORTING_ASSIGNEE", o => o.ErrorReportingAssignee, "error_reporting_assignee"),
        Whole("ERROR_REPORTING_MAX_ISSUES_PER_DAY", o => o.ErrorReportingMaxIssuesPerDay, 0, int.MaxValue, "error_reporting_max_issues_per_day"),
        Whole("ERROR_REPORTING_DEDUPE_WINDOW_HOURS", o => o.ErrorReportingDedupeWindowHours, 1, int.MaxValue, "error_reporting_dedupe_window_hours"),
        Flag("ERROR_REPORTING_COMMENT_ON_RECURRENCE", o => o.ErrorReportingCommentOnRecurrence, "error_reporting_comment_on_recurrence"),

        // Notifications and media-server rescans.
        Flag("NOTIFY_NTFY_ENABLED", o => o.NotifyNtfyEnabled, "notify_ntfy_enabled"),
        Text("NOTIFY_NTFY_URL", o => o.NotifyNtfyUrl, "notify_ntfy_url"),
        Text("NOTIFY_NTFY_TOPIC", o => o.NotifyNtfyTopic, "notify_ntfy_topic"),
        Text("NOTIFY_NTFY_TOKEN", o => o.NotifyNtfyToken, "notify_ntfy_token_set"),
        Text("NOTIFY_NTFY_EVENTS", o => o.NotifyNtfyEvents, "notify_ntfy_events"),
        Flag("NOTIFY_GOTIFY_ENABLED", o => o.NotifyGotifyEnabled, "notify_gotify_enabled"),
        Text("NOTIFY_GOTIFY_URL", o => o.NotifyGotifyUrl, "notify_gotify_url"),
        Text("NOTIFY_GOTIFY_TOKEN", o => o.NotifyGotifyToken, "notify_gotify_token_set"),
        Text("NOTIFY_GOTIFY_EVENTS", o => o.NotifyGotifyEvents, "notify_gotify_events"),
        Flag("NOTIFY_DISCORD_ENABLED", o => o.NotifyDiscordEnabled, "notify_discord_enabled"),
        Text("NOTIFY_DISCORD_WEBHOOK_URL", o => o.NotifyDiscordWebhookUrl, "notify_discord_webhook_url_set"),
        Text("NOTIFY_DISCORD_EVENTS", o => o.NotifyDiscordEvents, "notify_discord_events"),
        Flag("NOTIFY_WEBHOOK_ENABLED", o => o.NotifyWebhookEnabled, "notify_webhook_enabled"),
        Text("NOTIFY_WEBHOOK_URL", o => o.NotifyWebhookUrl, "notify_webhook_url_set"),
        Text("NOTIFY_WEBHOOK_EVENTS", o => o.NotifyWebhookEvents, "notify_webhook_events"),
        Flag("NOTIFY_KOMGA_ENABLED", o => o.NotifyKomgaEnabled, "notify_komga_enabled"),
        Text("NOTIFY_KOMGA_URL", o => o.NotifyKomgaUrl, "notify_komga_url"),
        Text("NOTIFY_KOMGA_API_KEY", o => o.NotifyKomgaApiKey, "notify_komga_api_key_set"),
        Text("NOTIFY_KOMGA_LIBRARY_ID", o => o.NotifyKomgaLibraryId, "notify_komga_library_id"),
        Text("NOTIFY_KOMGA_EVENTS", o => o.NotifyKomgaEvents, "notify_komga_events"),
        Flag("NOTIFY_KAVITA_ENABLED", o => o.NotifyKavitaEnabled, "notify_kavita_enabled"),
        Text("NOTIFY_KAVITA_URL", o => o.NotifyKavitaUrl, "notify_kavita_url"),
        Text("NOTIFY_KAVITA_API_KEY", o => o.NotifyKavitaApiKey, "notify_kavita_api_key_set"),
        Text("NOTIFY_KAVITA_LIBRARY_ID", o => o.NotifyKavitaLibraryId, "notify_kavita_library_id"),
        Text("NOTIFY_KAVITA_EVENTS", o => o.NotifyKavitaEvents, "notify_kavita_events"),
    ];

    /// <summary>The overrides whose variable currently forces a value.</summary>
    public static IReadOnlyList<EnvironmentOverride> Active() =>
        All.Where(o => o.TryGetForcedValue(out _)).ToArray();

    private static EnvironmentOverride Text(string variable, Expression<Func<AppSettings, string?>> setting, string? apiField = null) =>
        new(variable, PropertyOf(setting), apiField, value => value);

    /// <summary>An on/off setting: any value other than "true" (in any case) turns it off.</summary>
    private static EnvironmentOverride Flag(string variable, Expression<Func<AppSettings, bool>> setting, string? apiField = null) =>
        new(variable, PropertyOf(setting), apiField, value => value.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>A whole-number setting: anything unparsable or outside the range is ignored.</summary>
    private static EnvironmentOverride Whole(string variable, Expression<Func<AppSettings, int>> setting, int min, int max, string? apiField = null) =>
        new(variable, PropertyOf(setting), apiField, value => int.TryParse(value, out var number) && number >= min && number <= max ? number : null);

    private static PropertyInfo PropertyOf<T>(Expression<Func<AppSettings, T>> setting) =>
        (PropertyInfo)((MemberExpression)setting.Body).Member;
}
