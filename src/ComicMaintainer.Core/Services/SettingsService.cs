using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Notifications;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Service for persisting and managing application settings
/// Settings are stored in a JSON file in the Config directory
/// </summary>
public class SettingsService : ISettingsService
{
    private readonly ILogger<SettingsService> _logger;
    private readonly IOptionsMonitor<AppSettings> _appSettings;
    private readonly IConfigurationRoot _configuration;
    private readonly string _settingsFilePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Setting names whose values are secrets and must never be written to the log.
    /// </summary>
    private static readonly HashSet<string> SecretSettingNames = new(StringComparer.Ordinal)
    {
        "SmtpPassword",
        "ComicVineApiKey",
        "ErrorReportingGitHubToken",
        "SuwayomiPassword",
        "GitHubToken",
        "NotifyNtfyToken",
        "NotifyGotifyToken",
        "NotifyDiscordWebhookUrl",
        "NotifyWebhookUrl",
        "NotifyKomgaApiKey",
        "NotifyKavitaApiKey"
    };

    public SettingsService(
        ILogger<SettingsService> logger,
        IOptionsMonitor<AppSettings> appSettings,
        IConfigurationRoot configuration)
    {
        _logger = logger;
        _appSettings = appSettings;
        _configuration = configuration;
        
        var configDir = appSettings.CurrentValue.ConfigDirectory;
        
        // Ensure config directory exists
        try
        {
            Directory.CreateDirectory(configDir);
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogWarning("Unable to create config directory at {ConfigDir}, falling back to temp", configDir);
            configDir = Path.Join(Path.GetTempPath(), "ComicMaintainer");
            Directory.CreateDirectory(configDir);
        }
        
        _settingsFilePath = Path.Join(configDir, "user-settings.json");
        _logger.LogInformation("Settings will be persisted to: {SettingsPath}", _settingsFilePath);
    }

    public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(_appSettings.CurrentValue);
    }

    public async Task UpdateLogMaxBytesAsync(int maxBytes, CancellationToken cancellationToken = default)
    {
        ValidateLogMaxBytes(maxBytes);
        await SaveValueAsync("LogMaxBytes", maxBytes, cancellationToken);
    }

    public async Task UpdateFilenameFormatAsync(string format, CancellationToken cancellationToken = default)
    {
        ValidateFilenameFormat(format);
        await SaveValueAsync("FilenameFormat", format, cancellationToken);
    }

    public async Task UpdateIssueNumberPaddingAsync(int padding, CancellationToken cancellationToken = default)
    {
        ValidateIssueNumberPadding(padding);
        await SaveValueAsync("IssueNumberPadding", padding, cancellationToken);
    }

    public async Task UpdateWatcherEnableRenameAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await SaveValueAsync("WatcherEnableRename", enabled, cancellationToken);
    }

    public async Task UpdateWatcherEnableNormalizeAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await SaveValueAsync("WatcherEnableNormalize", enabled, cancellationToken);
    }

    public async Task UpdateConvertRarToCbzOnWriteAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await SaveValueAsync("ConvertRarToCbzOnWrite", enabled, cancellationToken);
    }

    public async Task UpdateWriteCoverToFirstArchiveAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await SaveValueAsync("WriteCoverToFirstArchive", enabled, cancellationToken);
    }

    public async Task UpdateWriteCoverToSeriesFolderAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await SaveValueAsync("WriteCoverToSeriesFolder", enabled, cancellationToken);
    }

    public async Task UpdateDownloadExternalSeriesImagesAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await SaveValueAsync("DownloadExternalSeriesImages", enabled, cancellationToken);
    }

    public async Task UpdateSeriesImageMaxBytesAsync(int maxBytes, CancellationToken cancellationToken = default)
    {
        if (maxBytes <= 0)
        {
            throw new SettingsValidationException("Series image max bytes must be greater than 0", nameof(maxBytes));
        }

        await SaveValueAsync("SeriesImageMaxBytes", maxBytes, cancellationToken);
    }

    public async Task UpdateSeriesImageMaxDownloadBytesAsync(int maxDownloadBytes, CancellationToken cancellationToken = default)
    {
        if (maxDownloadBytes <= 0)
        {
            throw new SettingsValidationException("Series image max download bytes must be greater than 0", nameof(maxDownloadBytes));
        }

        await SaveValueAsync("SeriesImageMaxDownloadBytes", maxDownloadBytes, cancellationToken);
    }

    public async Task UpdateSeriesImageMaxDimensionAsync(int maxDimension, CancellationToken cancellationToken = default)
    {
        if (maxDimension <= 0)
        {
            throw new SettingsValidationException("Series image max dimension must be greater than 0", nameof(maxDimension));
        }

        await SaveValueAsync("SeriesImageMaxDimension", maxDimension, cancellationToken);
    }

    public async Task UpdateSeriesImageLimitsAsync(
        int maxBytes,
        int maxDownloadBytes,
        int maxDimension,
        CancellationToken cancellationToken = default)
    {
        var values = NewValues();
        StageSeriesImageLimits(values, new SeriesImageLimitsUpdate(maxBytes, maxDownloadBytes, maxDimension), _appSettings.CurrentValue);
        await SaveValuesAsync(values, cancellationToken);
    }

    public async Task UpdateGitHubTokenAsync(string? token, CancellationToken cancellationToken = default)
    {
        await SaveValueAsync("GitHubToken", token, cancellationToken);
    }

    public async Task UpdateGitHubRepositoryAsync(string? repository, CancellationToken cancellationToken = default)
    {
        await SaveValueAsync("GitHubRepository", repository, cancellationToken);
    }

    public async Task UpdateGitHubIssueAssigneeAsync(string? assignee, CancellationToken cancellationToken = default)
    {
        await SaveValueAsync("GitHubIssueAssignee", assignee, cancellationToken);
    }

    public async Task UpdateDatabaseCleanupIntervalHoursAsync(int hours, CancellationToken cancellationToken = default)
    {
        if (hours < 0)
        {
            throw new SettingsValidationException("Cleanup interval hours must be non-negative (0 = only on startup)", nameof(hours));
        }

        await SaveValueAsync("DatabaseCleanupIntervalHours", hours, cancellationToken);
    }

    public async Task UpdateDatabaseCleanupSettingsAsync(DatabaseCleanupSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var values = NewValues();
        StageDatabaseCleanup(
            values,
            new DatabaseCleanupUpdate(
                settings.IntervalHours,
                settings.StaleCleanupMaxRemovalPercent,
                settings.StaleCleanupMissingGraceHours,
                settings.StaleCleanupSentinelFile ?? string.Empty,
                settings.ProcessingHistoryRetentionDays,
                settings.ProcessingHistoryMaxEntries));
        await SaveValuesAsync(values, cancellationToken);
    }

    public async Task UpdateDefaultLibraryViewAsync(string view, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(view))
        {
            throw new SettingsValidationException("View cannot be null or whitespace", nameof(view));
        }

        var normalized = view.Trim().ToLowerInvariant();
        if (normalized != "files" && normalized != "series")
        {
            throw new SettingsValidationException("View must be either 'files' or 'series'", nameof(view));
        }

        await SaveValueAsync("DefaultLibraryView", normalized, cancellationToken);
    }

    public async Task UpdateAllowRegistrationAsync(bool allowed, CancellationToken cancellationToken = default)
    {
        await SaveValueAsync("AllowRegistration", allowed, cancellationToken);
    }

    public async Task UpdateExternalSeriesMetadataEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await SaveValueAsync("EnableExternalSeriesMetadata", enabled, cancellationToken);
    }

    public async Task UpdateComicVineApiKeyAsync(string? apiKey, CancellationToken cancellationToken = default)
    {
        await SaveValueAsync("ComicVineApiKey", apiKey, cancellationToken);
    }

    public async Task UpdateComicVineBaseUrlAsync(string? baseUrl, CancellationToken cancellationToken = default)
    {
        await SaveValueAsync("ComicVineBaseUrl", baseUrl, cancellationToken);
    }

    public async Task UpdateMangaDexEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await SaveValueAsync("EnableMangaDexMetadata", enabled, cancellationToken);
    }

    public async Task UpdateMangaDexBaseUrlAsync(string? baseUrl, CancellationToken cancellationToken = default)
    {
        await SaveValueAsync("MangaDexBaseUrl", baseUrl, cancellationToken);
    }

    public async Task UpdateAniListEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await SaveValueAsync("EnableAniListMetadata", enabled, cancellationToken);
    }

    public async Task UpdateAniListBaseUrlAsync(string? baseUrl, CancellationToken cancellationToken = default)
    {
        await SaveValueAsync("AniListBaseUrl", baseUrl, cancellationToken);
    }

    public async Task UpdateExternalSeriesMetadataSettingsAsync(
        bool? enabled,
        string? comicVineApiKey,
        string? comicVineBaseUrl,
        bool? enableMangaDex,
        string? mangaDexBaseUrl,
        bool? enableAniList,
        string? aniListBaseUrl,
        CancellationToken cancellationToken = default)
    {
        var values = NewValues();
        StageExternalSeriesMetadata(
            values,
            new ExternalSeriesMetadataUpdate(
                enabled ?? LeftOut(nameof(AppSettings.EnableExternalSeriesMetadata), false),
                comicVineApiKey,
                comicVineBaseUrl,
                enableMangaDex ?? LeftOut(nameof(AppSettings.EnableMangaDexMetadata), false),
                mangaDexBaseUrl,
                enableAniList ?? LeftOut(nameof(AppSettings.EnableAniListMetadata), false),
                aniListBaseUrl));
        await SaveValuesAsync(values, cancellationToken);
    }

    public async Task UpdateSuwayomiSettingsAsync(
        bool? enabled,
        string? baseUrl,
        string? username,
        string? password,
        string? downloadsDirectory = null,
        string? importDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var values = NewValues();
        StageSuwayomi(
            values,
            new SuwayomiUpdate(
                enabled ?? LeftOut(nameof(AppSettings.EnableSuwayomi), false),
                baseUrl ?? LeftOut(nameof(AppSettings.SuwayomiBaseUrl), string.Empty),
                username ?? LeftOut(nameof(AppSettings.SuwayomiUsername), string.Empty),
                password,
                downloadsDirectory,
                importDirectory),
            _appSettings.CurrentValue);
        await SaveValuesAsync(values, cancellationToken);
    }

    public async Task UpdateDefaultPreferredLanguageAsync(string? language, CancellationToken cancellationToken = default)
    {
        // Null/empty is allowed (clears the default); otherwise validate
        // against the same allow-list the per-series API uses so the JSON
        // file never persists a code that no one will honour at read time.
        var normalized = Models.SeriesLanguagePreference.Normalize(language);
        if (normalized is null && !string.IsNullOrWhiteSpace(language))
        {
            throw new SettingsValidationException(
                $"Unsupported language '{language}'. Allowed values: {string.Join(", ", Models.SeriesLanguagePreference.AllowedLanguages)}.",
                nameof(language));
        }

        await SaveValueAsync("DefaultPreferredLanguage", normalized, cancellationToken);
    }

    public async Task UpdateEmailSettingsAsync(
        string? smtpHost,
        int? smtpPort,
        string? smtpUsername,
        string? smtpPassword,
        bool? smtpUseSsl,
        bool? smtpAllowInsecure,
        string? fromAddress,
        string? fromName,
        int? maxAttachmentMegabytes,
        CancellationToken cancellationToken = default)
    {
        var values = NewValues();
        StageEmail(
            values,
            new EmailUpdate(
                smtpHost ?? LeftOut(nameof(AppSettings.SmtpHost), string.Empty),
                smtpPort ?? LeftOut(nameof(AppSettings.SmtpPort), 587),
                smtpUsername ?? LeftOut(nameof(AppSettings.SmtpUsername), string.Empty),
                smtpPassword,
                smtpUseSsl ?? LeftOut(nameof(AppSettings.SmtpUseSsl), false),
                smtpAllowInsecure ?? LeftOut(nameof(AppSettings.SmtpAllowInsecure), false),
                fromAddress ?? LeftOut(nameof(AppSettings.EmailFromAddress), string.Empty),
                fromName ?? LeftOut(nameof(AppSettings.EmailFromName), string.Empty),
                maxAttachmentMegabytes ?? LeftOut(nameof(AppSettings.EmailMaxAttachmentMegabytes), 25)));
        await SaveValuesAsync(values, cancellationToken);
    }

    public async Task UpdateEmailRateLimitSettingsAsync(
        int? maxSendsPerHour,
        int? maxSendsPerDay,
        int? minSecondsBetweenSends,
        CancellationToken cancellationToken = default)
    {
        var values = NewValues();
        StageEmailRateLimit(
            values,
            new EmailRateLimitUpdate(
                maxSendsPerHour ?? LeftOut(nameof(AppSettings.EmailMaxSendsPerHour), 0),
                maxSendsPerDay ?? LeftOut(nameof(AppSettings.EmailMaxSendsPerDay), 0),
                minSecondsBetweenSends ?? LeftOut(nameof(AppSettings.EmailMinSecondsBetweenSends), 0)));
        await SaveValuesAsync(values, cancellationToken);
    }

    /// <summary>
    /// Persists the automated error-reporting settings.
    /// </summary>
    /// <remarks>
    /// The destination repository is not among them: reports always go to the
    /// project's own repository (see <c>ErrorReportingDestination</c>), so the
    /// only thing an operator supplies is the token that grants access to it.
    /// </remarks>
    public async Task UpdateErrorReportingSettingsAsync(
        bool? enabled,
        string? gitHubToken,
        string? assignee,
        int? maxIssuesPerDay,
        int? dedupeWindowHours,
        bool? commentOnRecurrence,
        CancellationToken cancellationToken = default)
    {
        var values = NewValues();
        StageErrorReporting(
            values,
            new ErrorReportingUpdate(
                enabled ?? LeftOut(nameof(AppSettings.ErrorReportingEnabled), false),
                gitHubToken,
                assignee ?? LeftOut(nameof(AppSettings.ErrorReportingAssignee), string.Empty),
                maxIssuesPerDay ?? LeftOut(nameof(AppSettings.ErrorReportingMaxIssuesPerDay), 10),
                dedupeWindowHours ?? LeftOut(nameof(AppSettings.ErrorReportingDedupeWindowHours), 24),
                commentOnRecurrence ?? LeftOut(nameof(AppSettings.ErrorReportingCommentOnRecurrence), true)),
            _appSettings.CurrentValue);
        await SaveValuesAsync(values, cancellationToken);
    }

    public async Task UpdateSettingsAsync(SettingsUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        // Every group is validated before anything is written, and they are all
        // written in one atomic file replacement: the save applies completely or
        // not at all, and live-reloading readers never see half of it.
        var values = NewValues();
        var current = _appSettings.CurrentValue;

        if (update.FilenameFormat is { } format)
        {
            ValidateFilenameFormat(format);
            values["FilenameFormat"] = format;
        }

        if (update.LogMaxBytes is { } logMaxBytes)
        {
            ValidateLogMaxBytes(logMaxBytes);
            values["LogMaxBytes"] = logMaxBytes;
        }

        if (update.IssueNumberPadding is { } padding)
        {
            ValidateIssueNumberPadding(padding);
            values["IssueNumberPadding"] = padding;
        }

        if (update.DatabaseCleanup is { } cleanup)
        {
            StageDatabaseCleanup(values, cleanup);
        }

        if (update.ExternalSeriesMetadata is { } metadata)
        {
            StageExternalSeriesMetadata(values, metadata);
        }

        if (update.SeriesImageLimits is { } imageLimits)
        {
            StageSeriesImageLimits(values, imageLimits, current);
        }

        if (update.Email is { } email)
        {
            StageEmail(values, email);
        }

        if (update.EmailRateLimit is { } rateLimit)
        {
            StageEmailRateLimit(values, rateLimit);
        }

        if (update.Suwayomi is { } suwayomi)
        {
            StageSuwayomi(values, suwayomi, current);
        }

        if (update.ErrorReporting is { } errorReporting)
        {
            StageErrorReporting(values, errorReporting, current);
        }

        if (update.Notifications is { } notifications)
        {
            StageNotifications(values, notifications, current);
        }

        await SaveValuesAsync(values, cancellationToken);
    }

    private static Dictionary<string, object?> NewValues() => new(StringComparer.Ordinal);

    /// <summary>The setting names of one notification target, and which of its fields it requires.</summary>
    private sealed record NotificationFields(
        string Target,
        string EnabledSetting,
        string UrlSetting,
        bool UrlIsSecret,
        string? TokenSetting,
        bool TokenRequired,
        string? TopicSetting,
        string? LibrarySetting,
        string EventsSetting);

    private static readonly NotificationFields[] NotificationTargetFields =
    [
        new(NotificationTarget.Ntfy, "NotifyNtfyEnabled", "NotifyNtfyUrl", false, "NotifyNtfyToken", false, "NotifyNtfyTopic", null, "NotifyNtfyEvents"),
        new(NotificationTarget.Gotify, "NotifyGotifyEnabled", "NotifyGotifyUrl", false, "NotifyGotifyToken", true, null, null, "NotifyGotifyEvents"),
        new(NotificationTarget.Discord, "NotifyDiscordEnabled", "NotifyDiscordWebhookUrl", true, null, false, null, null, "NotifyDiscordEvents"),
        new(NotificationTarget.Webhook, "NotifyWebhookEnabled", "NotifyWebhookUrl", true, null, false, null, null, "NotifyWebhookEvents"),
        new(NotificationTarget.Komga, "NotifyKomgaEnabled", "NotifyKomgaUrl", false, "NotifyKomgaApiKey", true, null, "NotifyKomgaLibraryId", "NotifyKomgaEvents"),
        new(NotificationTarget.Kavita, "NotifyKavitaEnabled", "NotifyKavitaUrl", false, "NotifyKavitaApiKey", true, null, "NotifyKavitaLibraryId", "NotifyKavitaEvents"),
    ];

    private static void StageNotifications(Dictionary<string, object?> values, NotificationsUpdate notifications, AppSettings current)
    {
        var updates = new[]
        {
            notifications.Ntfy, notifications.Gotify, notifications.Discord,
            notifications.Webhook, notifications.Komga, notifications.Kavita,
        };

        for (var i = 0; i < updates.Length; i++)
        {
            if (updates[i] is { } update)
            {
                StageNotificationTarget(values, NotificationTargetFields[i], update, current);
            }
        }
    }

    private static void StageNotificationTarget(
        Dictionary<string, object?> values,
        NotificationFields fields,
        NotificationTargetUpdate update,
        AppSettings current)
    {
        // A secret URL is left as it is: comparing it would let a caller test guesses at a pinned one.
        update = update with
        {
            Url = fields.UrlIsSecret ? update.Url : ExceptThePinnedValue(fields.UrlSetting, update.Url),
            Topic = fields.TopicSetting is null ? null : ExceptThePinnedValue(fields.TopicSetting, update.Topic),
            LibraryId = fields.LibrarySetting is null ? null : ExceptThePinnedValue(fields.LibrarySetting, update.LibraryId),
            Events = ExceptThePinnedValue(fields.EventsSetting, update.Events),
        };

        var stored = NotificationTargetOptions.For(current, fields.Target);
        var name = NotificationTarget.DisplayName(fields.Target);
        var url = Normalize(update.Url);
        if (url is not null && !fields.UrlIsSecret)
        {
            url = url.TrimEnd('/');
        }

        if (url is not null && !NotificationUrl.TryParse(url, out _))
        {
            // The value of a secret URL is not repeated back.
            throw new SettingsValidationException(
                fields.UrlIsSecret
                    ? $"The URL for {name} must be an absolute http(s) URL without a user name or password"
                    : $"'{url}' is not an absolute http(s) URL without a user name or password",
                "url");
        }

        var topic = Normalize(update.Topic);
        if (topic is not null && (topic.Length > 128 || topic.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))))
        {
            throw new SettingsValidationException("The ntfy topic must be at most 128 characters with no spaces", "topic");
        }

        var libraryId = Normalize(update.LibraryId);
        if (libraryId is not null)
        {
            var valid = fields.Target == NotificationTarget.Kavita
                ? int.TryParse(libraryId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0
                : libraryId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
            if (!valid)
            {
                throw new SettingsValidationException($"'{libraryId}' is not a valid library id for {name}", "libraryId");
            }
        }

        // Checked against what will be in effect after this save, so a pinned value the page
        // left out still counts. A save that touches none of these is not asked to prove it.
        var enabling = update.Enabled
            ?? (stored.Enabled && (update.Url is not null || update.Token is not null || update.Topic is not null));
        if (enabling)
        {
            if (string.IsNullOrWhiteSpace(update.Url is null ? stored.Url : url))
            {
                throw new SettingsValidationException($"A URL is required to enable {name}", "url");
            }

            if (fields.TopicSetting is not null && string.IsNullOrWhiteSpace(update.Topic is null ? stored.Topic : topic))
            {
                throw new SettingsValidationException($"A topic is required to enable {name}", "topic");
            }

            if (fields.TokenRequired && string.IsNullOrWhiteSpace(update.Token is null ? stored.Token : update.Token))
            {
                throw new SettingsValidationException($"A token or API key is required to enable {name}", "token");
            }
        }

        StageIfSet(values, fields.EnabledSetting, update.Enabled);
        if (update.Url is not null)
        {
            values[fields.UrlSetting] = url;
        }

        if (fields.TokenSetting is not null)
        {
            // A null token means "keep the stored secret"; an empty string clears it.
            StageSecretIfSet(values, fields.TokenSetting, update.Token);
        }

        if (fields.TopicSetting is not null)
        {
            StageTextIfSet(values, fields.TopicSetting, update.Topic);
        }

        if (fields.LibrarySetting is not null)
        {
            StageTextIfSet(values, fields.LibrarySetting, update.LibraryId);
        }

        if (update.Events is not null)
        {
            values[fields.EventsSetting] = NormalizeNotificationEvents(fields.Target, update.Events);
        }
    }

    /// <summary>The events in a fixed order, so a resave of the same choice changes nothing.</summary>
    private static string NormalizeNotificationEvents(string target, string events)
    {
        var allowed = NotificationEventType.AllowedFor(target);
        var requested = NotificationEventType.Split(events).ToList();
        if (requested.FirstOrDefault(e => !allowed.Contains(e)) is { } unknown)
        {
            throw new SettingsValidationException(
                $"'{unknown}' is not an event {NotificationTarget.DisplayName(target)} can receive. Allowed values: {string.Join(", ", allowed)}.",
                "events");
        }

        return string.Join(',', allowed.Where(requested.Contains));
    }

    private static void ValidateFilenameFormat(string format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            throw new SettingsValidationException("Format cannot be null or whitespace", nameof(format));
        }
    }

    private static void ValidateLogMaxBytes(int maxBytes)
    {
        if (maxBytes <= 0)
        {
            throw new SettingsValidationException("Max bytes must be greater than 0", nameof(maxBytes));
        }
    }

    private static void ValidateIssueNumberPadding(int padding)
    {
        if (padding is < 0 or > AppSettings.MaxIssueNumberPadding)
        {
            throw new SettingsValidationException(
                $"Padding must be between 0 and {AppSettings.MaxIssueNumberPadding}", nameof(padding));
        }
    }

    private static void StageSeriesImageLimits(Dictionary<string, object?> values, SeriesImageLimitsUpdate limits, AppSettings current)
    {
        limits = limits with
        {
            MaxBytes = ExceptThePinnedValue(nameof(AppSettings.SeriesImageMaxBytes), limits.MaxBytes),
            MaxDownloadBytes = ExceptThePinnedValue(nameof(AppSettings.SeriesImageMaxDownloadBytes), limits.MaxDownloadBytes),
        };

        if (limits.MaxBytes <= 0)
        {
            throw new SettingsValidationException("Series image max bytes must be greater than 0", "maxBytes");
        }

        if (limits.MaxDownloadBytes <= 0)
        {
            throw new SettingsValidationException("Series image max download bytes must be greater than 0", "maxDownloadBytes");
        }

        if (limits.MaxDimension <= 0)
        {
            throw new SettingsValidationException("Series image max dimension must be greater than 0", "maxDimension");
        }

        if ((limits.MaxBytes is not null || limits.MaxDownloadBytes is not null)
            && (limits.MaxDownloadBytes ?? current.SeriesImageMaxDownloadBytes) < (limits.MaxBytes ?? current.SeriesImageMaxBytes))
        {
            throw new SettingsValidationException("Series image max download bytes must be greater than or equal to series image max bytes", "maxDownloadBytes");
        }

        StageIfSet(values, "SeriesImageMaxBytes", limits.MaxBytes);
        StageIfSet(values, "SeriesImageMaxDownloadBytes", limits.MaxDownloadBytes);
        StageIfSet(values, "SeriesImageMaxDimension", limits.MaxDimension);
    }

    private static void StageDatabaseCleanup(Dictionary<string, object?> values, DatabaseCleanupUpdate settings)
    {
        settings = settings with
        {
            StaleCleanupSentinelFile = ExceptThePinnedValue(nameof(AppSettings.StaleCleanupSentinelFile), settings.StaleCleanupSentinelFile),
        };

        if (settings.IntervalHours < 0)
        {
            throw new SettingsValidationException("Cleanup interval hours must be non-negative (0 = only on startup)", nameof(settings));
        }

        if (settings.StaleCleanupMaxRemovalPercent is < 0 or > AppSettings.MaxStaleCleanupRemovalPercent)
        {
            throw new SettingsValidationException($"Max removal percent must be between 0 and {AppSettings.MaxStaleCleanupRemovalPercent}", nameof(settings));
        }

        if (settings.StaleCleanupMissingGraceHours is < 0 or > AppSettings.MaxStaleCleanupMissingGraceHours)
        {
            throw new SettingsValidationException($"Missing-file grace period must be between 0 and {AppSettings.MaxStaleCleanupMissingGraceHours} hours", nameof(settings));
        }

        if (settings.StaleCleanupSentinelFile is { } sentinelFile)
        {
            var sentinel = sentinelFile.Trim();
            if (sentinel.Length > DatabaseCleanupSettings.MaxSentinelFileLength || sentinel.Any(char.IsControl))
            {
                throw new SettingsValidationException($"Sentinel file must be a path of at most {DatabaseCleanupSettings.MaxSentinelFileLength} characters without control characters", nameof(settings));
            }

            // Saved as "" rather than null so clearing it also overrides appsettings.json.
            values["StaleCleanupSentinelFile"] = sentinel;
        }

        if (settings.ProcessingHistoryRetentionDays is < 0 or > AppSettings.MaxProcessingHistoryRetentionDays)
        {
            throw new SettingsValidationException($"History retention must be between 0 and {AppSettings.MaxProcessingHistoryRetentionDays} days (0 = keep regardless of age)", nameof(settings));
        }

        if (settings.ProcessingHistoryMaxEntries is < 0 or > AppSettings.MaxProcessingHistoryEntries)
        {
            throw new SettingsValidationException($"History max entries must be between 0 and {AppSettings.MaxProcessingHistoryEntries} (0 = no cap)", nameof(settings));
        }

        StageIfSet(values, "DatabaseCleanupIntervalHours", settings.IntervalHours);
        StageIfSet(values, "StaleCleanupMaxRemovalPercent", settings.StaleCleanupMaxRemovalPercent);
        StageIfSet(values, "StaleCleanupMissingGraceHours", settings.StaleCleanupMissingGraceHours);
        StageIfSet(values, "ProcessingHistoryRetentionDays", settings.ProcessingHistoryRetentionDays);
        StageIfSet(values, "ProcessingHistoryMaxEntries", settings.ProcessingHistoryMaxEntries);
    }

    private static void StageExternalSeriesMetadata(Dictionary<string, object?> values, ExternalSeriesMetadataUpdate metadata)
    {
        // The group is written in a single atomic file replacement. Saving the
        // fields one at a time let an aborted request or a failed write leave
        // lookups switched on beside the previous key and URLs.
        StageIfSet(values, "EnableExternalSeriesMetadata", metadata.Enabled);

        // A null key means "keep the stored secret"; a blank one clears it.
        StageTextIfSet(values, "ComicVineApiKey", metadata.ComicVineApiKey);

        // A blank base URL keeps the stored one.
        StageIfNotBlank(values, "ComicVineBaseUrl", metadata.ComicVineBaseUrl);
        StageIfSet(values, "EnableMangaDexMetadata", metadata.EnableMangaDex);
        StageIfNotBlank(values, "MangaDexBaseUrl", metadata.MangaDexBaseUrl);
        StageIfSet(values, "EnableAniListMetadata", metadata.EnableAniList);
        StageIfNotBlank(values, "AniListBaseUrl", metadata.AniListBaseUrl);
        StageIfSet(values, "FillComicInfoFromProviders", metadata.FillComicInfoFromProviders);
    }

    private static void StageSuwayomi(Dictionary<string, object?> values, SuwayomiUpdate suwayomi, AppSettings current)
    {
        suwayomi = suwayomi with
        {
            BaseUrl = ExceptThePinnedValue(nameof(AppSettings.SuwayomiBaseUrl), suwayomi.BaseUrl),
            DownloadsDirectory = ExceptThePinnedValue(nameof(AppSettings.SuwayomiDownloadsDirectory), suwayomi.DownloadsDirectory),
            ImportDirectory = ExceptThePinnedValue(nameof(AppSettings.SuwayomiImportDirectory), suwayomi.ImportDirectory),
        };

        var normalizedUrl = string.IsNullOrWhiteSpace(suwayomi.BaseUrl) ? null : suwayomi.BaseUrl.Trim().TrimEnd('/');

        // Checked against the URL in effect after this save, so a pinned URL the page
        // left out still counts. A save that touches neither is not asked to prove it.
        var enabling = suwayomi.Enabled ?? (current.EnableSuwayomi && suwayomi.BaseUrl is not null);
        var effectiveUrl = suwayomi.BaseUrl is null ? current.SuwayomiBaseUrl : normalizedUrl;
        if (enabling && string.IsNullOrWhiteSpace(effectiveUrl))
        {
            throw new SettingsValidationException("A Suwayomi server URL is required to enable the integration", "baseUrl");
        }

        if (normalizedUrl is not null && !SuwayomiService.TryBuildEndpoint(normalizedUrl, out _))
        {
            throw new SettingsValidationException($"'{normalizedUrl}' is not a valid http(s) URL", "baseUrl");
        }

        // The import folders are validated against the current library
        // directories so a typo or an overlapping folder is reported now
        // rather than silently skipped by every scheduled import.
        var normalizedDownloadsDirectory = Normalize(suwayomi.DownloadsDirectory);
        if (normalizedDownloadsDirectory is not null
            && !SuwayomiDownloadImportService.TryResolveDownloadsDirectory(normalizedDownloadsDirectory, current, out _, out var downloadsError))
        {
            throw new SettingsValidationException(downloadsError!, "downloadsDirectory");
        }

        var normalizedImportDirectory = Normalize(suwayomi.ImportDirectory);
        if (normalizedImportDirectory is not null
            && !SuwayomiDownloadImportService.TryResolveImportDirectory(normalizedImportDirectory, current, out _, out var importError))
        {
            throw new SettingsValidationException(importError!, "importDirectory");
        }

        StageIfSet(values, "EnableSuwayomi", suwayomi.Enabled);
        if (suwayomi.BaseUrl is not null)
        {
            values["SuwayomiBaseUrl"] = normalizedUrl;
        }

        StageTextIfSet(values, "SuwayomiUsername", suwayomi.Username);

        // A null password means "keep the stored secret"; an empty string clears it.
        StageSecretIfSet(values, "SuwayomiPassword", suwayomi.Password);

        // Likewise a null folder keeps the stored value, so API clients that
        // predate the import settings do not wipe them on every save.
        StageTextIfSet(values, "SuwayomiDownloadsDirectory", suwayomi.DownloadsDirectory);
        StageTextIfSet(values, "SuwayomiImportDirectory", suwayomi.ImportDirectory);
    }

    private static void StageEmail(Dictionary<string, object?> values, EmailUpdate email)
    {
        email = email with
        {
            EmailFromAddress = ExceptThePinnedValue(nameof(AppSettings.EmailFromAddress), email.EmailFromAddress),
            EmailMaxAttachmentMb = ExceptThePinnedValue(nameof(AppSettings.EmailMaxAttachmentMegabytes), email.EmailMaxAttachmentMb),
        };

        if (email.SmtpPort is < 1 or > 65535)
        {
            throw new SettingsValidationException("SMTP port must be between 1 and 65535", "smtpPort");
        }

        if (email.EmailMaxAttachmentMb is < 1 or > 200)
        {
            throw new SettingsValidationException("Max attachment size must be between 1 and 200 MB", "maxAttachmentMegabytes");
        }

        var normalizedFrom = Normalize(email.EmailFromAddress);
        if (normalizedFrom is not null && !EmailAddressUtils.IsValid(normalizedFrom))
        {
            throw new SettingsValidationException($"'{normalizedFrom}' is not a valid email address", "fromAddress");
        }

        StageTextIfSet(values, "SmtpHost", email.SmtpHost);
        StageIfSet(values, "SmtpPort", email.SmtpPort);
        StageTextIfSet(values, "SmtpUsername", email.SmtpUsername);
        StageIfSet(values, "SmtpUseSsl", email.SmtpUseSsl);
        StageIfSet(values, "SmtpAllowInsecure", email.SmtpAllowInsecure);
        StageTextIfSet(values, "EmailFromAddress", email.EmailFromAddress);
        if (email.EmailFromName is not null)
        {
            values["EmailFromName"] = Normalize(email.EmailFromName) ?? "ComicMaintainer";
        }

        StageIfSet(values, "EmailMaxAttachmentMegabytes", email.EmailMaxAttachmentMb);

        // A null password means "keep the stored secret"; an empty string clears it.
        StageSecretIfSet(values, "SmtpPassword", email.SmtpPassword);
    }

    private static void StageEmailRateLimit(Dictionary<string, object?> values, EmailRateLimitUpdate rateLimit)
    {
        if (rateLimit.MaxSendsPerHour is < 0 or > Models.EmailRateLimitBounds.MaxSendsPerHour)
        {
            throw new SettingsValidationException(
                $"Maximum emails per hour must be between 0 and {Models.EmailRateLimitBounds.MaxSendsPerHour}",
                "maxSendsPerHour");
        }

        if (rateLimit.MaxSendsPerDay is < 0 or > Models.EmailRateLimitBounds.MaxSendsPerDay)
        {
            throw new SettingsValidationException(
                $"Maximum emails per day must be between 0 and {Models.EmailRateLimitBounds.MaxSendsPerDay}",
                "maxSendsPerDay");
        }

        if (rateLimit.MinSecondsBetweenSends is < 0 or > Models.EmailRateLimitBounds.MaxMinSecondsBetweenSends)
        {
            throw new SettingsValidationException(
                $"Minimum seconds between emails must be between 0 and {Models.EmailRateLimitBounds.MaxMinSecondsBetweenSends}",
                "minSecondsBetweenSends");
        }

        // Written together so the delivery queue, which re-reads the limits
        // while it waits, never sees half of an update.
        StageIfSet(values, "EmailMaxSendsPerHour", rateLimit.MaxSendsPerHour);
        StageIfSet(values, "EmailMaxSendsPerDay", rateLimit.MaxSendsPerDay);
        StageIfSet(values, "EmailMinSecondsBetweenSends", rateLimit.MinSecondsBetweenSends);
    }

    private static void StageErrorReporting(Dictionary<string, object?> values, ErrorReportingUpdate reporting, AppSettings current)
    {
        reporting = reporting with
        {
            Assignee = ExceptThePinnedValue(nameof(AppSettings.ErrorReportingAssignee), reporting.Assignee),
            MaxIssuesPerDay = ExceptThePinnedValue(nameof(AppSettings.ErrorReportingMaxIssuesPerDay), reporting.MaxIssuesPerDay),
            DedupeWindowHours = ExceptThePinnedValue(nameof(AppSettings.ErrorReportingDedupeWindowHours), reporting.DedupeWindowHours),
        };

        var normalizedAssignee = Normalize(reporting.Assignee);

        if (normalizedAssignee is not null && !GitHubNameValidator.IsValidOwner(normalizedAssignee))
        {
            throw new SettingsValidationException($"'{normalizedAssignee}' is not a valid GitHub login", "assignee");
        }

        // Turning reporting on without a token would queue reports that can
        // never be delivered, so refuse rather than fail silently later. When
        // the switch is left out, only a change to the token can break it.
        var enabling = reporting.Enabled ?? (current.ErrorReportingEnabled && reporting.GitHubToken is not null);
        if (enabling && !HasUsableToken(reporting.GitHubToken, current))
        {
            throw new SettingsValidationException("A GitHub token is required to enable error reporting", "gitHubToken");
        }

        if (reporting.MaxIssuesPerDay is < 0 or > 100)
        {
            throw new SettingsValidationException("Maximum issues per day must be between 0 and 100", "maxIssuesPerDay");
        }

        if (reporting.DedupeWindowHours is < 1 or > 720)
        {
            throw new SettingsValidationException("Dedupe window must be between 1 and 720 hours", "dedupeWindowHours");
        }

        // The whole reporting block is written in a single atomic file
        // replacement. Writing the settings one at a time would let the
        // live-reloading sink observe reporting switched on beside a token that
        // has not been written yet — and a failure partway through would leave
        // that mixed state active.
        StageTextIfSet(values, "ErrorReportingAssignee", reporting.Assignee);
        StageIfSet(values, "ErrorReportingMaxIssuesPerDay", reporting.MaxIssuesPerDay);
        StageIfSet(values, "ErrorReportingDedupeWindowHours", reporting.DedupeWindowHours);
        StageIfSet(values, "ErrorReportingCommentOnRecurrence", reporting.CommentOnRecurrence);
        StageIfSet(values, "ErrorReportingEnabled", reporting.Enabled);

        // A null token means "keep the stored secret"; an empty string clears it.
        if (reporting.GitHubToken is not null)
        {
            values["ErrorReportingGitHubToken"] = reporting.GitHubToken.Length == 0 ? null : reporting.GitHubToken.Trim();
        }
    }

    /// <summary>
    /// What a field left out of a group-endpoint request stands for: <paramref name="unset"/> (blank,
    /// off or a default, as those endpoints have always read it), or null, which keeps the value,
    /// when the environment pins the setting. Otherwise a client would be refused over a field it
    /// never sent.
    /// </summary>
    private static T? LeftOut<T>(string settingName, T unset)
        where T : struct
        => IsPinned(settingName) ? null : unset;

    /// <inheritdoc cref="LeftOut{T}(string, T)"/>
    private static string? LeftOut(string settingName, string unset)
        => IsPinned(settingName) ? null : unset;

    /// <summary>
    /// Null when <paramref name="value"/> is what the environment already pins the setting to.
    /// Applied before a group's checks to the fields where the environment accepts more than a
    /// save does (EMAIL_MAX_ATTACHMENT_MB=500), so a client sending a whole group back is not
    /// refused for a value it cannot change.
    /// </summary>
    private static T? ExceptThePinnedValue<T>(string settingName, T? value)
        where T : struct
        => value is { } set && IsPinnedTo(settingName, set) ? null : value;

    /// <inheritdoc cref="ExceptThePinnedValue{T}(string, T?)"/>
    private static string? ExceptThePinnedValue(string settingName, string? value)
        => value is not null && IsPinnedTo(settingName, value) ? null : value;

    private static bool IsPinned(string settingName) =>
        AppSettingsEnvironmentOverrides.Active().Any(o => o.SettingName == settingName);

    private static bool IsPinnedTo(string settingName, object value) =>
        AppSettingsEnvironmentOverrides.All.Any(o =>
            o.SettingName == settingName && o.TryGetForcedValue(out var forced) && IsSameValue(forced, value));

    /// <summary>Stages a value unless it is null, which means "keep the current value".</summary>
    private static void StageIfSet<T>(Dictionary<string, object?> values, string settingName, T? value)
        where T : struct
    {
        if (value is { } set)
        {
            values[settingName] = set;
        }
    }

    /// <summary>Stages a trimmed text value: null keeps the current value, blank clears it.</summary>
    private static void StageTextIfSet(Dictionary<string, object?> values, string settingName, string? value)
    {
        if (value is not null)
        {
            values[settingName] = Normalize(value);
        }
    }

    /// <summary>Stages a text value only when it is not blank: a blank one keeps the current value.</summary>
    private static void StageIfNotBlank(Dictionary<string, object?> values, string settingName, string? value)
    {
        if (Normalize(value) is { } normalized)
        {
            values[settingName] = normalized;
        }
    }

    /// <summary>Stages a secret exactly as given, never trimmed: null keeps it, an empty string clears it.</summary>
    private static void StageSecretIfSet(Dictionary<string, object?> values, string settingName, string? value)
    {
        if (value is not null)
        {
            values[settingName] = value.Length == 0 ? null : value;
        }
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// True when a token is available to report with: one supplied in this
    /// request, one supplied by the environment, or one already stored. A null
    /// request token means "keep the stored one".
    /// </summary>
    private static bool HasUsableToken(string? requestedToken, AppSettings current) =>
        !string.IsNullOrWhiteSpace(requestedToken)
        || IsPinned(nameof(AppSettings.ErrorReportingGitHubToken))
        || (requestedToken is null && !string.IsNullOrWhiteSpace(current.ErrorReportingGitHubToken));

    private Task SaveValueAsync(string settingName, object? value, CancellationToken cancellationToken)
    {
        var values = NewValues();
        values[settingName] = value;
        return SaveValuesAsync(values, cancellationToken);
    }

    /// <summary>
    /// Saves settings in one atomic write, so they are saved entirely or not at all,
    /// then logs each one. Secret values are never passed to the logger, only whether
    /// they were set or cleared.
    /// </summary>
    /// <exception cref="SettingsLockedException">A value would change a setting the environment pins.</exception>
    private async Task SaveValuesAsync(Dictionary<string, object?> values, CancellationToken cancellationToken)
    {
        DropUnchangedPinnedValues(values);
        if (values.Count == 0)
        {
            return;
        }

        await PersistSettingsAsync(values, cancellationToken);

        foreach (var (settingName, value) in values)
        {
            if (SecretSettingNames.Contains(settingName))
            {
                _logger.LogInformation(
                    "Updated setting {SettingName} ({State})",
                    settingName,
                    string.IsNullOrEmpty(value as string) ? "cleared" : "value hidden");
                continue;
            }

            // Sanitize value for logging to prevent log forging.
            var sanitizedValue = LoggingHelper.SanitizeForLog(value?.ToString() ?? "null");
            _logger.LogInformation("Updated setting {SettingName} to {Value}", settingName, sanitizedValue);
        }
    }

    /// <summary>
    /// Refuses to save a setting an environment variable pins, because the saved value
    /// would never take effect. A value equal to the pinned one changes nothing, so a
    /// client that sends a whole group back is not refused for it; it is dropped instead
    /// of copying the environment's value into the file. A secret is never compared: the
    /// answer would let a caller test guesses at it, so any write to a pinned one is refused.
    /// </summary>
    private static void DropUnchangedPinnedValues(Dictionary<string, object?> values)
    {
        var refused = new List<EnvironmentOverride>();
        foreach (var environmentOverride in AppSettingsEnvironmentOverrides.All)
        {
            if (!values.TryGetValue(environmentOverride.SettingName, out var requested)
                || !environmentOverride.TryGetForcedValue(out var forced))
            {
                continue;
            }

            if (SecretSettingNames.Contains(environmentOverride.SettingName) || !IsSameValue(forced, requested))
            {
                refused.Add(environmentOverride);
            }
            else
            {
                values.Remove(environmentOverride.SettingName);
            }
        }

        if (refused.Count > 0)
        {
            throw new SettingsLockedException(refused);
        }
    }

    /// <summary>
    /// Compares a pinned value with a requested one. Text is compared as the save would
    /// store it, trimmed and without a trailing slash, since a pinned value is not normalized.
    /// </summary>
    private static bool IsSameValue(object? forced, object? requested) =>
        forced is string
            ? CanonicalText(forced as string) == CanonicalText(requested as string)
            : Equals(forced, requested);

    private static string CanonicalText(string? value) => (value ?? string.Empty).Trim().TrimEnd('/');

    /// <summary>
    /// Writes one or more settings in a single atomic replacement of
    /// user-settings.json, so a reader that reloads on change never observes a
    /// partially applied group of related settings.
    /// </summary>
    private async Task PersistSettingsAsync(Dictionary<string, object?> values, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Read existing user-settings.json which is rooted under "AppSettings" so the
            // configuration provider can bind it directly to AppSettings via reload-on-change.
            Dictionary<string, object?> appSettingsSection;

            if (File.Exists(_settingsFilePath))
            {
                var json = await File.ReadAllTextAsync(_settingsFilePath, cancellationToken);
                appSettingsSection = ReadAppSettingsSection(json);
            }
            else
            {
                appSettingsSection = new Dictionary<string, object?>(StringComparer.Ordinal);
            }

            // Update the settings
            foreach (var (settingName, value) in values)
            {
                OnStagingSetting(settingName);
                appSettingsSection[settingName] = value;
            }

            // Serialize and write atomically (temp file + move) so the configuration provider's
            // file watcher does not observe a half-written file.
            var root = new Dictionary<string, object?>
            {
                ["AppSettings"] = appSettingsSection
            };
            var updatedJson = JsonSerializer.Serialize(root, _jsonOptions);

            var tempPath = _settingsFilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, updatedJson, cancellationToken);
            File.Move(tempPath, _settingsFilePath, overwrite: true);

            // Inside the lock so concurrent saves reload in the order they wrote.
            ReloadConfiguration();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A cancelled save is the client going away, not a failure.
            _logger.LogError(ex, "Failed to persist settings {SettingNames}", string.Join(", ", values.Keys));
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Makes a save visible through <see cref="IOptionsMonitor{TOptions}"/> before it returns.
    /// The configuration provider's file watcher (kept for hand edits of the file) would apply
    /// it too, but only after its debounce, and the caller would meanwhile read the old values.
    /// </summary>
    private void ReloadConfiguration()
    {
        try
        {
            _configuration.Reload();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The file is saved, so the save stands; the file watcher applies it shortly.
            _logger.LogWarning(ex, "The saved settings could not be reloaded immediately");
        }
    }

    /// <summary>
    /// Called inside <see cref="PersistSettingsAsync"/> just before each setting is
    /// staged for the write, so tests can interrupt a save between two fields.
    /// </summary>
    protected virtual void OnStagingSetting(string settingName)
    {
    }

    /// <summary>
    /// Read the existing user-settings.json content into a mutable dictionary representing
    /// the AppSettings section. Supports both the new object-rooted shape ({ "AppSettings": {...} })
    /// and the legacy flat shape (top-level keys) for backward compatibility.
    /// </summary>
    private static Dictionary<string, object?> ReadAppSettingsSection(string json)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            // New shape: { "AppSettings": { ... } }
            if (root.TryGetProperty("AppSettings", out var appSettingsElement) &&
                appSettingsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in appSettingsElement.EnumerateObject())
                {
                    result[prop.Name] = ConvertJsonElement(prop.Value);
                }
                return result;
            }

            // Legacy flat shape: top-level keys are the setting names
            foreach (var prop in root.EnumerateObject())
            {
                result[prop.Name] = ConvertJsonElement(prop.Value);
            }
        }
        catch (JsonException)
        {
            // Corrupt or unreadable file — start with an empty section. The next write
            // will replace the file with a valid one.
        }

        return result;
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            // For arrays/objects, round-trip via raw JSON so they are re-serialized verbatim.
            _ => JsonDocument.Parse(element.GetRawText()).RootElement.Clone(),
        };
    }
}
