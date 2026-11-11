using System.Data.Common;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Authorization;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SettingsController : ControllerBase
{
    private const int BYTES_PER_MB = 1048576;

    /// <summary>Largest log size that still fits an int when converted to bytes.</summary>
    private const int MaxLogMegabytes = 2047;

    private readonly IOptionsMonitor<AppSettings> _appSettings;
    private readonly ILogger<SettingsController> _logger;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly IBatchJobRegistry _jobRegistry;
    private readonly IFileStoreService _fileStore;
    private readonly ISettingsService _settingsService;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ISeriesLanguagePreferenceRetagService _languageRetag;
    private readonly IEventBroadcaster _eventBroadcaster;
    private readonly IProcessingHistoryService _processingHistory;
    private readonly IAuthorizationService _authorization;

    public SettingsController(
        IOptionsMonitor<AppSettings> appSettings, 
        ILogger<SettingsController> logger,
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        IBatchJobRegistry jobRegistry,
        IFileStoreService fileStore,
        ISettingsService settingsService,
        IHostApplicationLifetime applicationLifetime,
        ISeriesLanguagePreferenceRetagService languageRetag,
        IEventBroadcaster eventBroadcaster,
        IProcessingHistoryService processingHistory,
        IAuthorizationService authorization)
    {
        _appSettings = appSettings;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _jobRegistry = jobRegistry;
        _fileStore = fileStore;
        _settingsService = settingsService;
        _applicationLifetime = applicationLifetime;
        _languageRetag = languageRetag;
        _eventBroadcaster = eventBroadcaster;
        _processingHistory = processingHistory;
        _authorization = authorization;
    }

    // RESTful endpoint: GET /api/settings - Get all settings
    [HttpGet]
    public async Task<ActionResult<object>> GetAllSettings()
    {
        _logger.LogInformation("GetAllSettings endpoint called");

        var isAdministrator = (await _authorization.AuthorizeAsync(User, AuthorizationPolicies.CanAdminister)).Succeeded;

        var settings = new
        {
            filename_format = _appSettings.CurrentValue.FilenameFormat,
            issue_number_padding = _appSettings.CurrentValue.IssueNumberPadding,
            watcher_enable_rename = _appSettings.CurrentValue.WatcherEnableRename,
            watcher_enable_normalize = _appSettings.CurrentValue.WatcherEnableNormalize,
            convert_rar_to_cbz_on_write = _appSettings.CurrentValue.ConvertRarToCbzOnWrite,
            log_max_bytes = _appSettings.CurrentValue.LogMaxBytes,
            database_cleanup_interval_hours = _appSettings.CurrentValue.DatabaseCleanupIntervalHours,
            stale_cleanup_max_removal_percent = _appSettings.CurrentValue.StaleCleanupMaxRemovalPercent,
            stale_cleanup_missing_grace_hours = _appSettings.CurrentValue.StaleCleanupMissingGraceHours,
            stale_cleanup_sentinel_file = _appSettings.CurrentValue.StaleCleanupSentinelFile ?? string.Empty,
            processing_history_retention_days = _appSettings.CurrentValue.ProcessingHistoryRetentionDays,
            processing_history_max_entries = _appSettings.CurrentValue.ProcessingHistoryMaxEntries,
            enable_external_series_metadata = _appSettings.CurrentValue.EnableExternalSeriesMetadata,
            // As with the SMTP password, the key itself is never returned; the
            // UI only needs to know whether one is stored.
            comicvine_api_key_set = !string.IsNullOrEmpty(_appSettings.CurrentValue.ComicVineApiKey),
            comicvine_base_url = _appSettings.CurrentValue.ComicVineBaseUrl,
            enable_mangadex_metadata = _appSettings.CurrentValue.EnableMangaDexMetadata,
            mangadex_base_url = _appSettings.CurrentValue.MangaDexBaseUrl,
            enable_anilist_metadata = _appSettings.CurrentValue.EnableAniListMetadata,
            anilist_base_url = _appSettings.CurrentValue.AniListBaseUrl,
            fill_comicinfo_from_providers = _appSettings.CurrentValue.FillComicInfoFromProviders,
            enable_suwayomi = _appSettings.CurrentValue.EnableSuwayomi,
            suwayomi_base_url = _appSettings.CurrentValue.SuwayomiBaseUrl,
            suwayomi_username = _appSettings.CurrentValue.SuwayomiUsername,
            // As with the SMTP password, the password itself is never returned.
            suwayomi_password_set = !string.IsNullOrEmpty(_appSettings.CurrentValue.SuwayomiPassword),
            suwayomi_downloads_directory = _appSettings.CurrentValue.SuwayomiDownloadsDirectory,
            suwayomi_import_directory = _appSettings.CurrentValue.SuwayomiImportDirectory,
            allow_registration = _appSettings.CurrentValue.AllowRegistration,
            default_preferred_language = _appSettings.CurrentValue.DefaultPreferredLanguage,
            write_cover_to_first_archive = _appSettings.CurrentValue.WriteCoverToFirstArchive,
            write_cover_to_series_folder = _appSettings.CurrentValue.WriteCoverToSeriesFolder,
            download_external_series_images = _appSettings.CurrentValue.DownloadExternalSeriesImages,
            series_image_max_bytes = _appSettings.CurrentValue.SeriesImageMaxBytes,
            series_image_max_download_bytes = _appSettings.CurrentValue.SeriesImageMaxDownloadBytes,
            series_image_max_dimension = _appSettings.CurrentValue.SeriesImageMaxDimension,
            smtp_host = _appSettings.CurrentValue.SmtpHost,
            smtp_port = _appSettings.CurrentValue.SmtpPort,
            smtp_username = _appSettings.CurrentValue.SmtpUsername,
            // The password itself is never returned; the UI only needs to know
            // whether one is stored so it can show a "leave blank to keep" hint.
            smtp_password_set = !string.IsNullOrEmpty(_appSettings.CurrentValue.SmtpPassword),
            smtp_use_ssl = _appSettings.CurrentValue.SmtpUseSsl,
            smtp_allow_insecure = _appSettings.CurrentValue.SmtpAllowInsecure,
            email_from_address = _appSettings.CurrentValue.EmailFromAddress,
            email_from_name = _appSettings.CurrentValue.EmailFromName,
            email_max_attachment_mb = _appSettings.CurrentValue.EmailMaxAttachmentMegabytes,
            email_max_sends_per_hour = _appSettings.CurrentValue.EmailMaxSendsPerHour,
            email_max_sends_per_day = _appSettings.CurrentValue.EmailMaxSendsPerDay,
            email_min_seconds_between_sends = _appSettings.CurrentValue.EmailMinSecondsBetweenSends,
            error_reporting_enabled = _appSettings.CurrentValue.ErrorReportingEnabled,
            // The destination is fixed, not a setting. It is still returned so
            // the UI can name the repository it files issues in without
            // duplicating it client-side.
            error_reporting_github_owner = ErrorReportingDestination.Owner,
            error_reporting_github_repo = ErrorReportingDestination.Repo,
            // As with the SMTP password, the token itself is never returned; the
            // UI only needs to know whether one is stored.
            error_reporting_github_token_set = !string.IsNullOrEmpty(_appSettings.CurrentValue.ErrorReportingGitHubToken),
            error_reporting_assignee = _appSettings.CurrentValue.ErrorReportingAssignee,
            error_reporting_max_issues_per_day = _appSettings.CurrentValue.ErrorReportingMaxIssuesPerDay,
            error_reporting_dedupe_window_hours = _appSettings.CurrentValue.ErrorReportingDedupeWindowHours,
            error_reporting_comment_on_recurrence = _appSettings.CurrentValue.ErrorReportingCommentOnRecurrence,
            // Notification targets. As with the SMTP password, tokens, API keys and webhook
            // URLs (which carry a token) are never returned; only whether one is stored.
            notify_ntfy_enabled = _appSettings.CurrentValue.NotifyNtfyEnabled,
            notify_ntfy_url = _appSettings.CurrentValue.NotifyNtfyUrl,
            // On a public ntfy server knowing the topic is enough to subscribe to it, so it goes
            // only to those who can change it.
            notify_ntfy_topic = isAdministrator ? _appSettings.CurrentValue.NotifyNtfyTopic : string.Empty,
            notify_ntfy_token_set = !string.IsNullOrEmpty(_appSettings.CurrentValue.NotifyNtfyToken),
            notify_ntfy_events = _appSettings.CurrentValue.NotifyNtfyEvents,
            notify_gotify_enabled = _appSettings.CurrentValue.NotifyGotifyEnabled,
            notify_gotify_url = _appSettings.CurrentValue.NotifyGotifyUrl,
            notify_gotify_token_set = !string.IsNullOrEmpty(_appSettings.CurrentValue.NotifyGotifyToken),
            notify_gotify_events = _appSettings.CurrentValue.NotifyGotifyEvents,
            notify_discord_enabled = _appSettings.CurrentValue.NotifyDiscordEnabled,
            notify_discord_webhook_url_set = !string.IsNullOrEmpty(_appSettings.CurrentValue.NotifyDiscordWebhookUrl),
            notify_discord_events = _appSettings.CurrentValue.NotifyDiscordEvents,
            notify_webhook_enabled = _appSettings.CurrentValue.NotifyWebhookEnabled,
            notify_webhook_url_set = !string.IsNullOrEmpty(_appSettings.CurrentValue.NotifyWebhookUrl),
            notify_webhook_events = _appSettings.CurrentValue.NotifyWebhookEvents,
            notify_komga_enabled = _appSettings.CurrentValue.NotifyKomgaEnabled,
            notify_komga_url = _appSettings.CurrentValue.NotifyKomgaUrl,
            notify_komga_api_key_set = !string.IsNullOrEmpty(_appSettings.CurrentValue.NotifyKomgaApiKey),
            notify_komga_library_id = _appSettings.CurrentValue.NotifyKomgaLibraryId,
            notify_komga_events = _appSettings.CurrentValue.NotifyKomgaEvents,
            notify_kavita_enabled = _appSettings.CurrentValue.NotifyKavitaEnabled,
            notify_kavita_url = _appSettings.CurrentValue.NotifyKavitaUrl,
            notify_kavita_api_key_set = !string.IsNullOrEmpty(_appSettings.CurrentValue.NotifyKavitaApiKey),
            notify_kavita_library_id = _appSettings.CurrentValue.NotifyKavitaLibraryId,
            notify_kavita_events = _appSettings.CurrentValue.NotifyKavitaEvents,
            opds_enabled = _appSettings.CurrentValue.OpdsEnabled,
            // The settings the environment pins: one entry per field above, naming the variable.
            env_locked = EnvLockEntries(AppSettingsEnvironmentOverrides.Active())
        };

        _logger.LogDebug("Returning settings: FilenameFormat={FilenameFormat}, IssueNumberPadding={IssueNumberPadding}, WatcherEnableRename={WatcherEnableRename}, WatcherEnableNormalize={WatcherEnableNormalize}, LogMaxBytes={LogMaxBytes}, DatabaseCleanupIntervalHours={DatabaseCleanupIntervalHours}",
            settings.filename_format, settings.issue_number_padding, settings.watcher_enable_rename, 
            settings.watcher_enable_normalize, settings.log_max_bytes, settings.database_cleanup_interval_hours);

        return Ok(settings);
    }

    /// <summary>
    /// Saves every settings group in one write: the request applies completely or, on a 400
    /// or 409, not at all, and the new values are in effect when it returns. Every group and
    /// field is optional; see <see cref="SettingsUpdate"/> for how a value is kept or cleared.
    /// </summary>
    [HttpPut]
    public async Task<ActionResult> UpdateAllSettings([FromBody] AllSettingsRequest request, CancellationToken cancellationToken = default)
    {
        if (request.LogMaxMB is <= 0 or > MaxLogMegabytes)
        {
            return BadRequestProblem($"MaxMB must be between 1 and {MaxLogMegabytes}");
        }

        var update = new SettingsUpdate
        {
            FilenameFormat = request.FilenameFormat,
            LogMaxBytes = ToBytes(request.LogMaxMB),
            IssueNumberPadding = request.IssueNumberPadding,
            DatabaseCleanup = request.DatabaseCleanup,
            ExternalSeriesMetadata = request.ExternalSeriesMetadata,
            SeriesImageLimits = request.SeriesImageLimits is { } limits
                ? new SeriesImageLimitsUpdate(ToBytes(limits.MaxMB), ToBytes(limits.MaxDownloadMB), limits.MaxDimension)
                : null,
            Email = request.Email,
            EmailRateLimit = request.EmailRateLimit,
            Suwayomi = request.Suwayomi,
            ErrorReporting = request.ErrorReporting,
            Notifications = request.Notifications,
            Opds = request.Opds
        };

        return await SaveAsync(
            () => _settingsService.UpdateSettingsAsync(update, cancellationToken),
            "Settings saved");
    }

    [HttpGet("filename-format")]
    public ActionResult<object> GetFilenameFormat()
    {
        return Ok(new { format = _appSettings.CurrentValue.FilenameFormat, @default = AppSettings.DefaultFilenameFormat });
    }

    // RESTful endpoint: PUT /api/settings/filename-format
    [HttpPut("filename-format")]
    public async Task<ActionResult> UpdateFilenameFormat([FromBody] FilenameFormatRequest request, CancellationToken cancellationToken = default)
    {
        var sanitizedFormat = LoggingHelper.SanitizeForLog(request.Format);
        _logger.LogInformation("Filename format update requested: {Format}", sanitizedFormat);

        return await SaveAsync(
            () => _settingsService.UpdateFilenameFormatAsync(request.Format, cancellationToken),
            "Filename format updated successfully");
    }

    [HttpGet("issue-number-padding")]
    public ActionResult<object> GetIssueNumberPadding()
    {
        return Ok(new { padding = _appSettings.CurrentValue.IssueNumberPadding });
    }

    // RESTful endpoint: PUT /api/settings/issue-number-padding
    [HttpPut("issue-number-padding")]
    public async Task<ActionResult> UpdateIssueNumberPadding([FromBody] IssueNumberPaddingRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Issue number padding update requested: {Padding}", request.Padding);

        return await SaveAsync(
            () => _settingsService.UpdateIssueNumberPaddingAsync(request.Padding, cancellationToken),
            "Issue number padding updated successfully");
    }

    // Note: Master watcher enabled has been removed. Watcher is now enabled
    // when either WatcherEnableRename or WatcherEnableNormalize is true.

    [HttpGet("log-max-bytes")]
    public ActionResult<object> GetLogMaxBytes()
    {
        return Ok(new { maxMB = _appSettings.CurrentValue.LogMaxBytes / (double)BYTES_PER_MB });
    }

    // RESTful endpoint: PUT /api/settings/log-max-bytes
    [HttpPut("log-max-bytes")]
    public async Task<ActionResult> UpdateLogMaxBytes([FromBody] LogMaxBytesRequest request, CancellationToken cancellationToken = default)
    {
        // Validate MaxMB to prevent integer overflow (max ~2047 MB for int.MaxValue)
        if (request.MaxMB is <= 0 or > MaxLogMegabytes)
        {
            return BadRequestProblem($"MaxMB must be between 1 and {MaxLogMegabytes}");
        }

        var maxBytes = (int)ToBytes(request.MaxMB)!;
        _logger.LogInformation("Log max bytes update requested: {MaxMB} MB ({MaxBytes} bytes)", request.MaxMB, maxBytes);

        return await SaveAsync(
            async () =>
            {
                await _settingsService.UpdateLogMaxBytesAsync(maxBytes, cancellationToken);
                _logger.LogWarning("Log max bytes updated to {MaxMB} MB ({MaxBytes} bytes). Restart the application for the change to take effect.", request.MaxMB, maxBytes);
            },
            "Log max bytes updated successfully. Restart required for changes to take effect.");
    }

    [HttpGet("watcher-enable-rename")]
    public ActionResult<object> GetWatcherEnableRename()
    {
        return Ok(new { enabled = _appSettings.CurrentValue.WatcherEnableRename });
    }

    [HttpPut("watcher-enable-rename")]
    public async Task<ActionResult> UpdateWatcherEnableRename([FromBody] WatcherEnableRenameRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Watcher enable rename update requested: {Enabled}", request.Enabled);

        return await SaveAsync(
            () => _settingsService.UpdateWatcherEnableRenameAsync(request.Enabled, cancellationToken),
            "Watcher enable rename updated successfully");
    }

    [HttpGet("watcher-enable-normalize")]
    public ActionResult<object> GetWatcherEnableNormalize()
    {
        return Ok(new { enabled = _appSettings.CurrentValue.WatcherEnableNormalize });
    }

    [HttpPut("watcher-enable-normalize")]
    public async Task<ActionResult> UpdateWatcherEnableNormalize([FromBody] WatcherEnableNormalizeRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Watcher enable normalize update requested: {Enabled}", request.Enabled);

        return await SaveAsync(
            () => _settingsService.UpdateWatcherEnableNormalizeAsync(request.Enabled, cancellationToken),
            "Watcher enable normalize updated successfully");
    }

    [HttpGet("convert-rar-to-cbz-on-write")]
    public ActionResult<object> GetConvertRarToCbzOnWrite()
    {
        return Ok(new { enabled = _appSettings.CurrentValue.ConvertRarToCbzOnWrite });
    }

    [HttpPut("convert-rar-to-cbz-on-write")]
    public async Task<ActionResult> UpdateConvertRarToCbzOnWrite([FromBody] ConvertRarToCbzOnWriteRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Convert CBR/RAR to CBZ on write update requested: {Enabled}", request.Enabled);

        return await SaveAsync(
            () => _settingsService.UpdateConvertRarToCbzOnWriteAsync(request.Enabled, cancellationToken),
            "Convert CBR/RAR to CBZ on write updated successfully");
    }

    [HttpGet("write-cover-to-first-archive")]
    public ActionResult<object> GetWriteCoverToFirstArchive()
    {
        return Ok(new { enabled = _appSettings.CurrentValue.WriteCoverToFirstArchive });
    }

    [HttpPut("write-cover-to-first-archive")]
    public async Task<ActionResult> UpdateWriteCoverToFirstArchive([FromBody] WriteCoverToFirstArchiveRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Write cover to first archive update requested: {Enabled}", request.Enabled);

        return await SaveAsync(
            () => _settingsService.UpdateWriteCoverToFirstArchiveAsync(request.Enabled, cancellationToken),
            "Write cover to first archive updated successfully");
    }

    [HttpGet("write-cover-to-series-folder")]
    public ActionResult<object> GetWriteCoverToSeriesFolder()
    {
        return Ok(new { enabled = _appSettings.CurrentValue.WriteCoverToSeriesFolder });
    }

    [HttpPut("write-cover-to-series-folder")]
    public async Task<ActionResult> UpdateWriteCoverToSeriesFolder([FromBody] WriteCoverToSeriesFolderRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Write cover to series folder update requested: {Enabled}", request.Enabled);

        return await SaveAsync(
            () => _settingsService.UpdateWriteCoverToSeriesFolderAsync(request.Enabled, cancellationToken),
            "Write cover to series folder updated successfully");
    }

    [HttpGet("download-external-series-images")]
    public ActionResult<object> GetDownloadExternalSeriesImages()
    {
        return Ok(new { enabled = _appSettings.CurrentValue.DownloadExternalSeriesImages });
    }

    [HttpPut("download-external-series-images")]
    public async Task<ActionResult> UpdateDownloadExternalSeriesImages([FromBody] DownloadExternalSeriesImagesRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Download external series images update requested: {Enabled}", request.Enabled);

        return await SaveAsync(
            () => _settingsService.UpdateDownloadExternalSeriesImagesAsync(request.Enabled, cancellationToken),
            "Download external series images updated successfully");
    }

    [HttpGet("series-image-limits")]
    public ActionResult<object> GetSeriesImageLimits()
    {
        return Ok(new
        {
            maxMB = _appSettings.CurrentValue.SeriesImageMaxBytes / (double)BYTES_PER_MB,
            maxDownloadMB = _appSettings.CurrentValue.SeriesImageMaxDownloadBytes / (double)BYTES_PER_MB,
            maxDimension = _appSettings.CurrentValue.SeriesImageMaxDimension
        });
    }

    [HttpPut("series-image-limits")]
    public async Task<ActionResult> UpdateSeriesImageLimits([FromBody] SeriesImageLimitsRequest request, CancellationToken cancellationToken = default)
    {
        if (request.MaxMB <= 0 || request.MaxDownloadMB <= 0 || request.MaxDimension <= 0)
        {
            return BadRequestProblem("Series image limits must all be greater than 0");
        }

        var maxBytes = (int)ToBytes(request.MaxMB)!;
        var maxDownloadBytes = (int)ToBytes(request.MaxDownloadMB)!;

        _logger.LogInformation("Series image limits update requested: MaxBytes={MaxBytes}, MaxDownloadBytes={MaxDownloadBytes}, MaxDimension={MaxDimension}",
            maxBytes, maxDownloadBytes, request.MaxDimension);

        return await SaveAsync(
            () => _settingsService.UpdateSeriesImageLimitsAsync(maxBytes, maxDownloadBytes, request.MaxDimension, cancellationToken),
            "Series image limits updated successfully");
    }

    [HttpGet("database-cleanup-interval-hours")]
    public ActionResult<object> GetDatabaseCleanupIntervalHours()
    {
        return Ok(new { hours = _appSettings.CurrentValue.DatabaseCleanupIntervalHours });
    }

    [HttpPut("database-cleanup-interval-hours")]
    public async Task<ActionResult> UpdateDatabaseCleanupIntervalHours([FromBody] DatabaseCleanupIntervalRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Database cleanup interval update requested: {Hours} hours", request.Hours);

        return await SaveAsync(
            () => _settingsService.UpdateDatabaseCleanupIntervalHoursAsync(request.Hours, cancellationToken),
            "Database cleanup interval updated successfully");
    }

    /// <summary>
    /// Every database-cleanup setting: the interval, the guards that keep the stale-entry
    /// cleanup from emptying the library, and the processing-history retention.
    /// </summary>
    [HttpGet("database-cleanup")]
    public ActionResult<object> GetDatabaseCleanupSettings()
    {
        var settings = _appSettings.CurrentValue;
        return Ok(new
        {
            intervalHours = settings.DatabaseCleanupIntervalHours,
            staleCleanupMaxRemovalPercent = settings.StaleCleanupMaxRemovalPercent,
            staleCleanupMissingGraceHours = settings.StaleCleanupMissingGraceHours,
            staleCleanupSentinelFile = settings.StaleCleanupSentinelFile ?? string.Empty,
            processingHistoryRetentionDays = settings.ProcessingHistoryRetentionDays,
            processingHistoryMaxEntries = settings.ProcessingHistoryMaxEntries
        });
    }

    /// <summary>Saves every database-cleanup setting in one write; 400 names the first value out of range.</summary>
    [HttpPut("database-cleanup")]
    public async Task<ActionResult> UpdateDatabaseCleanupSettings([FromBody] DatabaseCleanupSettingsRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return BadRequestProblem("Request body is required");
        }

        // A field the request leaves out keeps its current value, so a script changing one
        // setting cannot silently clear the sentinel file or reset another guard.
        var current = _appSettings.CurrentValue;
        var settings = new DatabaseCleanupSettings(
            request.IntervalHours ?? current.DatabaseCleanupIntervalHours,
            request.StaleCleanupMaxRemovalPercent ?? current.StaleCleanupMaxRemovalPercent,
            request.StaleCleanupMissingGraceHours ?? current.StaleCleanupMissingGraceHours,
            request.StaleCleanupSentinelFile ?? current.StaleCleanupSentinelFile,
            request.ProcessingHistoryRetentionDays ?? current.ProcessingHistoryRetentionDays,
            request.ProcessingHistoryMaxEntries ?? current.ProcessingHistoryMaxEntries);

        _logger.LogInformation(
            "Database cleanup settings update requested: IntervalHours={IntervalHours}, MaxRemovalPercent={Percent}, MissingGraceHours={GraceHours}, HistoryRetentionDays={Days}, HistoryMaxEntries={MaxEntries}",
            settings.IntervalHours, settings.StaleCleanupMaxRemovalPercent, settings.StaleCleanupMissingGraceHours,
            settings.ProcessingHistoryRetentionDays, settings.ProcessingHistoryMaxEntries);

        return await SaveAsync(
            () => _settingsService.UpdateDatabaseCleanupSettingsAsync(settings, cancellationToken),
            "Database cleanup settings updated successfully");
    }

    [HttpGet("allow-registration")]
    public ActionResult<object> GetAllowRegistration()
    {
        return Ok(new { enabled = _appSettings.CurrentValue.AllowRegistration });
    }

    [HttpPut("allow-registration")]
    public async Task<ActionResult> UpdateAllowRegistration([FromBody] AllowRegistrationRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            return BadRequestProblem("Enabled is required");
        }

        _logger.LogInformation("Self-service registration update requested: {Enabled}", request.Enabled);

        return await SaveAsync(
            () => _settingsService.UpdateAllowRegistrationAsync(request.Enabled, cancellationToken),
            "Self-service registration updated successfully");
    }

    [HttpGet("default-preferred-language")]
    public ActionResult<object> GetDefaultPreferredLanguage()
    {
        return Ok(new { language = _appSettings.CurrentValue.DefaultPreferredLanguage });
    }

    [HttpPut("default-preferred-language")]
    public async Task<ActionResult> UpdateDefaultPreferredLanguage([FromBody] DefaultPreferredLanguageRequest request, CancellationToken cancellationToken = default)
    {
        var previous = Core.Models.SeriesLanguagePreference.Normalize(_appSettings.CurrentValue.DefaultPreferredLanguage);
        var requested = Core.Models.SeriesLanguagePreference.Normalize(request?.Language);

        var result = await SaveAsync(
            () => _settingsService.UpdateDefaultPreferredLanguageAsync(request?.Language, cancellationToken),
            "Default preferred language updated successfully");

        // If the effective default actually changed, flag every series that does NOT have a
        // per-series override (those are unaffected by the global default) for back-fill. The
        // scheduled backfill job rewrites the on-disk <Series> tags later.
        if (result is OkObjectResult
            && !string.Equals(previous, requested, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var flaggedCount = await _languageRetag.QueueRetagForGlobalDefaultAsync(cancellationToken);
                if (flaggedCount > 0)
                {
                    _logger.LogInformation(
                        "Flagged {FileCount} file(s) for metadata backfill after global default preferred-language change from '{Previous}' to '{Updated}'",
                        flaggedCount,
                        LoggingHelper.SanitizeForLog(previous),
                        LoggingHelper.SanitizeForLog(requested));
                }
            }
            catch (Exception ex) when (ex is DbException or DbUpdateException or IOException)
            {
                // The setting is saved; a later library scan still corrects the tags.
                _logger.LogWarning(ex,
                    "Failed to flag files for metadata backfill after global default preferred-language change");
            }
        }

        return result;
    }

    /// <summary>
    /// Updates the external series metadata settings. Omit <c>comicVineApiKey</c>
    /// to keep the stored key; send an empty string to clear it. The settings are
    /// saved together: if the request fails, none of them change.
    /// </summary>
    [HttpPut("external-series-metadata")]
    public async Task<ActionResult> UpdateExternalSeriesMetadata([FromBody] ExternalSeriesMetadataSettingsRequest request, CancellationToken cancellationToken = default)
    {
        return await SaveAsync(
            () => _settingsService.UpdateExternalSeriesMetadataSettingsAsync(
                request.Enabled,
                request.ComicVineApiKey,
                request.ComicVineBaseUrl,
                request.EnableMangaDex,
                request.MangaDexBaseUrl,
                request.EnableAniList,
                request.AniListBaseUrl,
                cancellationToken),
            "External series metadata settings updated successfully");
    }

    /// <summary>
    /// Removes database rows for files that no longer exist on disk. Refuses (409) while the
    /// library looks unmounted, and asks for confirmation (409 with
    /// <c>requiresConfirmation</c>; repeat with <c>?confirm=true</c>) before removing more
    /// than the configured share of the library; see <see cref="StaleCleanupResponses"/>.
    /// The file store notifies clients when rows were removed.
    /// </summary>
    [HttpPost("cleanup-database")]
    public async Task<ActionResult> CleanupDatabase([FromQuery] bool confirm = false, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Manual database cleanup requested (confirmed: {Confirmed})", confirm);
        var result = await _fileStore.CleanupStaleEntriesAsync(new StaleCleanupOptions(IsManual: true, Confirmed: confirm), cancellationToken);
        return StaleCleanupResponses.ToActionResult(this, result);
    }

    [HttpPost("reset")]
    public async Task<ActionResult> ResetDatabase(CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Database reset requested - this will delete all processing data");

        // Cancel all active jobs before the data they work on is deleted.
        // Cooperative: each job stops at its next cancellation check.
        var cancelledJobs = _jobRegistry.CancelActiveJobs();
        _logger.LogInformation("Cancelled {Count} active job(s) due to reset", cancelledJobs);

        // Clear all database tables
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        _logger.LogInformation("Deleting processing history...");
        await dbContext.ProcessingHistory.ExecuteDeleteAsync(cancellationToken);
        _processingHistory.InvalidateTotalCount();

        _logger.LogInformation("Deleting comic files...");
        await dbContext.ComicFiles.ExecuteDeleteAsync(cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        // Reinitialize file store from database (which is now empty)
        await _fileStore.InitializeFromDatabaseAsync(cancellationToken);

        _logger.LogInformation("Database reset completed successfully");

        // Notify connected clients that the file list has changed so they can refresh their
        // views in place rather than requiring a full page reload. Broadcasts never throw.
        await _eventBroadcaster.BroadcastFileListUpdateAsync();

        return Ok(new { success = true, message = "Database reset completed successfully" });
    }

    [HttpPost("restart")]
    [Authorize(Roles = "Admin")]
    public ActionResult RestartApplication()
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var userName = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value;

        _logger.LogWarning("Application restart requested by user {UserId} ({UserName})", userId, userName);

        // Trigger graceful shutdown which will cause the application to restart (when running in a container or with a process manager)
        _logger.LogInformation("Initiating application shutdown for restart...");

        // Use a background task to allow the response to be sent before shutting down
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000); // Give time for the response to be sent
            _applicationLifetime.StopApplication();
        });

        return Ok(new
        {
            success = true,
            message = "Application restart initiated. The application will shut down gracefully and restart if running in a container or with a process manager."
        });
    }

    /// <summary>
    /// Runs a settings write and answers with its outcome. Only the two failures a client can
    /// act on are answered here; anything else (a full disk, a bug) reaches
    /// <see cref="GlobalExceptionHandler"/>, which logs it and answers without internal details.
    /// </summary>
    private async Task<ActionResult> SaveAsync(Func<Task> save, string successMessage)
    {
        try
        {
            await save();
        }
        catch (SettingsValidationException ex)
        {
            return BadRequestProblem(ex.Reason);
        }
        catch (SettingsLockedException ex)
        {
            var problem = SettingsProblem(StatusCodes.Status409Conflict, "The setting is set by the environment.", ex.Message);
            problem.Extensions["env_locked"] = EnvLockEntries(ex.Overrides);
            return Conflict(problem);
        }

        return Ok(new { message = successMessage });
    }

    private BadRequestObjectResult BadRequestProblem(string error) =>
        BadRequest(SettingsProblem(StatusCodes.Status400BadRequest, "The settings were not saved.", error));

    /// <summary>A problem document that also carries the <c>error</c> string every client of this API reads.</summary>
    private static ProblemDetails SettingsProblem(int status, string title, string error)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = error };
        problem.Extensions["error"] = error;
        return problem;
    }

    private static object[] EnvLockEntries(IEnumerable<EnvironmentOverride> overrides) =>
        overrides
            .Where(o => o.ApiField is not null)
            .Select(o => (object)new { field = o.ApiField, variable = o.Variable })
            .ToArray();

    private static int? ToBytes(double? megabytes) =>
        megabytes is { } value ? (int)Math.Round(value * BYTES_PER_MB) : null;

    /// <summary>Body of <c>PUT /api/settings</c>; see <see cref="SettingsUpdate"/>.</summary>
    public class AllSettingsRequest
    {
        public string? FilenameFormat { get; set; }

        /// <summary>Largest log file before rotation, in megabytes.</summary>
        public double? LogMaxMB { get; set; }

        public int? IssueNumberPadding { get; set; }
        public DatabaseCleanupUpdate? DatabaseCleanup { get; set; }
        public ExternalSeriesMetadataUpdate? ExternalSeriesMetadata { get; set; }
        public AllSettingsImageLimits? SeriesImageLimits { get; set; }
        public EmailUpdate? Email { get; set; }
        public EmailRateLimitUpdate? EmailRateLimit { get; set; }
        public SuwayomiUpdate? Suwayomi { get; set; }
        public ErrorReportingUpdate? ErrorReporting { get; set; }
        public NotificationsUpdate? Notifications { get; set; }
        public OpdsUpdate? Opds { get; set; }
    }

    /// <summary>The series cover image limits of <see cref="AllSettingsRequest"/>, with sizes in megabytes.</summary>
    public class AllSettingsImageLimits
    {
        public double? MaxMB { get; set; }
        public double? MaxDownloadMB { get; set; }
        public int? MaxDimension { get; set; }
    }

    public class FilenameFormatRequest
    {
        public string Format { get; set; } = string.Empty;
    }

    public class IssueNumberPaddingRequest
    {
        public int Padding { get; set; }
    }

    public class LogMaxBytesRequest
    {
        public double MaxMB { get; set; }
    }

    public class WatcherEnableRenameRequest
    {
        public bool Enabled { get; set; }
    }

    public class WatcherEnableNormalizeRequest
    {
        public bool Enabled { get; set; }
    }

    public class ConvertRarToCbzOnWriteRequest
    {
        public bool Enabled { get; set; }
    }

    public class WriteCoverToFirstArchiveRequest
    {
        public bool Enabled { get; set; }
    }

    public class WriteCoverToSeriesFolderRequest
    {
        public bool Enabled { get; set; }
    }

    public class DownloadExternalSeriesImagesRequest
    {
        public bool Enabled { get; set; }
    }

    public class SeriesImageLimitsRequest
    {
        /// <summary>Maximum size of a stored series cover image, in megabytes.</summary>
        public double MaxMB { get; set; }

        /// <summary>Maximum raw download payload size, in megabytes.</summary>
        public double MaxDownloadMB { get; set; }

        /// <summary>Maximum width or height of a stored series cover image, in pixels.</summary>
        public int MaxDimension { get; set; }
    }

    public class DatabaseCleanupIntervalRequest
    {
        public int Hours { get; set; }
    }

    /// <summary>
    /// Body of <c>PUT /api/settings/database-cleanup</c>; an omitted value keeps its current setting.
    /// </summary>
    public class DatabaseCleanupSettingsRequest
    {
        /// <summary>Hours between scheduled cleanups; 0 runs it only once after start-up.</summary>
        public int? IntervalHours { get; set; }

        /// <summary>Largest share of the library (0-100 percent) one cleanup may remove without confirmation.</summary>
        public int? StaleCleanupMaxRemovalPercent { get; set; }

        /// <summary>Hours a file must stay missing before a cleanup removes its row without confirmation.</summary>
        public int? StaleCleanupMissingGraceHours { get; set; }

        /// <summary>File that must exist before any cleanup runs (relative to the watched directory); empty disables it.</summary>
        public string? StaleCleanupSentinelFile { get; set; }

        /// <summary>Processing-history entries older than this many days are pruned; 0 keeps them regardless of age.</summary>
        public int? ProcessingHistoryRetentionDays { get; set; }

        /// <summary>Only the newest this many processing-history entries are kept; 0 disables the cap.</summary>
        public int? ProcessingHistoryMaxEntries { get; set; }
    }

    public class AllowRegistrationRequest
    {
        public bool Enabled { get; set; }
    }

    public class DefaultPreferredLanguageRequest
    {
        /// <summary>One of <c>en</c>, <c>ja</c>, <c>ko</c>, <c>zh</c>, or null/empty to clear.</summary>
        public string? Language { get; set; }
    }


    /// <summary>
    /// Updates the SMTP settings used to email comics to saved ereader devices.
    /// Omit <c>smtpPassword</c> to keep the stored password; send an empty string to clear it.
    /// Any other field left out is read as blank, off or its default, except that a setting
    /// the environment pins is left alone. The other group endpoints work the same way.
    /// </summary>
    [HttpPut("email")]
    public async Task<ActionResult> UpdateEmailSettings([FromBody] EmailSettingsRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return BadRequestProblem("Request body is required");
        }

        return await SaveAsync(
            () => _settingsService.UpdateEmailSettingsAsync(
                request.SmtpHost,
                request.SmtpPort,
                request.SmtpUsername,
                request.SmtpPassword,
                request.SmtpUseSsl,
                request.SmtpAllowInsecure,
                request.EmailFromAddress,
                request.EmailFromName,
                request.EmailMaxAttachmentMb,
                cancellationToken),
            "Email settings updated successfully");
    }

    public class EmailSettingsRequest
    {
        public string? SmtpHost { get; set; }

        /// <summary>SMTP port; 587 when left out.</summary>
        public int? SmtpPort { get; set; }

        public string? SmtpUsername { get; set; }

        /// <summary>Null keeps the stored password; empty string clears it.</summary>
        public string? SmtpPassword { get; set; }

        /// <summary>Connect with implicit TLS (SMTPS, usually port 465).</summary>
        public bool? SmtpUseSsl { get; set; }

        /// <summary>
        /// Allow a plaintext connection when the server does not advertise
        /// STARTTLS. Only enable for a trusted local relay.
        /// </summary>
        public bool? SmtpAllowInsecure { get; set; }

        public string? EmailFromAddress { get; set; }
        public string? EmailFromName { get; set; }

        /// <summary>Largest attachment in megabytes; 25 when left out.</summary>
        public int? EmailMaxAttachmentMb { get; set; }
    }

    /// <summary>
    /// Updates the email rate limit. Each value is a non-negative integer and
    /// 0 disables that limit.
    /// </summary>
    [HttpPut("email-rate-limit")]
    public async Task<ActionResult> UpdateEmailRateLimitSettings(
        [FromBody] EmailRateLimitSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return BadRequestProblem("Request body is required");
        }

        return await SaveAsync(
            () => _settingsService.UpdateEmailRateLimitSettingsAsync(
                request.MaxSendsPerHour,
                request.MaxSendsPerDay,
                request.MinSecondsBetweenSends,
                cancellationToken),
            "Email rate limit updated successfully");
    }

    public class EmailRateLimitSettingsRequest
    {
        /// <summary>Most comic emails in any rolling 60-minute window; 0 for no limit.</summary>
        public int? MaxSendsPerHour { get; set; }

        /// <summary>Most comic emails in any rolling 24-hour window; 0 for no limit.</summary>
        public int? MaxSendsPerDay { get; set; }

        /// <summary>Minimum gap between two comic emails; 0 to send back to back.</summary>
        public int? MinSecondsBetweenSends { get; set; }
    }

    /// <summary>
    /// Updates the Suwayomi download-server settings. Omit <c>password</c> to
    /// keep the stored password; send an empty string to clear it. The
    /// <c>downloadsDirectory</c> and <c>importDirectory</c> import folders
    /// follow the same omit-to-keep, empty-to-clear rule.
    /// </summary>
    [HttpPut("suwayomi")]
    public async Task<ActionResult> UpdateSuwayomiSettings([FromBody] SuwayomiSettingsRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return BadRequestProblem("Request body is required");
        }

        return await SaveAsync(
            () => _settingsService.UpdateSuwayomiSettingsAsync(
                request.Enabled,
                request.BaseUrl,
                request.Username,
                request.Password,
                downloadsDirectory: request.DownloadsDirectory,
                importDirectory: request.ImportDirectory,
                cancellationToken: cancellationToken),
            "Suwayomi settings updated successfully");
    }

    public class SuwayomiSettingsRequest
    {
        public bool? Enabled { get; set; }

        /// <summary>Base URL of the Suwayomi server, e.g. <c>http://localhost:4567</c>.</summary>
        public string? BaseUrl { get; set; }

        /// <summary>Optional user name when the server requires basic auth.</summary>
        public string? Username { get; set; }

        /// <summary>Null keeps the stored password; empty string clears it.</summary>
        public string? Password { get; set; }

        /// <summary>
        /// Folder Suwayomi saves chapters into, as seen by this service. Null
        /// keeps the stored value; empty string clears it (disabling the import).
        /// </summary>
        public string? DownloadsDirectory { get; set; }

        /// <summary>
        /// Library folder imported chapters are moved into (relative to the
        /// watched directory or absolute inside it). Null keeps the stored
        /// value; empty string clears it (the watched directory itself).
        /// </summary>
        public string? ImportDirectory { get; set; }
    }

    /// <summary>
    /// Updates the automated error-reporting settings. Omit <c>gitHubToken</c>
    /// to keep the stored token; send an empty string to clear it. The
    /// destination repository is fixed and cannot be changed here.
    /// </summary>
    [HttpPut("error-reporting")]
    public async Task<ActionResult> UpdateErrorReportingSettings([FromBody] ErrorReportingSettingsRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return BadRequestProblem("Request body is required");
        }

        return await SaveAsync(
            () => _settingsService.UpdateErrorReportingSettingsAsync(
                request.Enabled,
                request.GitHubToken,
                request.Assignee,
                request.MaxIssuesPerDay,
                request.DedupeWindowHours,
                request.CommentOnRecurrence,
                cancellationToken),
            "Error reporting settings updated successfully");
    }

    public class ErrorReportingSettingsRequest
    {
        public bool? Enabled { get; set; }

        /// <summary>Null keeps the stored token; empty string clears it.</summary>
        public string? GitHubToken { get; set; }

        /// <summary>Optional GitHub login new issues are assigned to.</summary>
        public string? Assignee { get; set; }

        /// <summary>Most issues opened in a day; 10 when left out.</summary>
        public int? MaxIssuesPerDay { get; set; }

        /// <summary>Hours a repeat of the same error counts as a recurrence; 24 when left out.</summary>
        public int? DedupeWindowHours { get; set; }

        /// <summary>Comment on the open issue when an error recurs; on when left out.</summary>
        public bool? CommentOnRecurrence { get; set; }
    }

    public class ExternalSeriesMetadataSettingsRequest
    {
        public bool? Enabled { get; set; }

        /// <summary>Null keeps the stored key; an empty (or blank) string clears it.</summary>
        public string? ComicVineApiKey { get; set; }

        public string? ComicVineBaseUrl { get; set; }
        public bool? EnableMangaDex { get; set; }
        public string? MangaDexBaseUrl { get; set; }
        public bool? EnableAniList { get; set; }
        public string? AniListBaseUrl { get; set; }
    }
}
