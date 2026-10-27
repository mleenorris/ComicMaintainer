using ComicMaintainer.Core.Configuration;

namespace ComicMaintainer.WebApi.Startup;

/// <summary>
/// Applies environment-variable overrides to <see cref="AppSettings"/> so they always win
/// over values from appsettings.json and user-settings.json. Implemented as
/// <see cref="Microsoft.Extensions.Options.IPostConfigureOptions{TOptions}"/> so it runs
/// every time the options instance is (re)built, including after a user-settings.json reload.
/// </summary>
internal sealed class AppSettingsEnvironmentPostConfigure : Microsoft.Extensions.Options.IPostConfigureOptions<AppSettings>
{
    public void PostConfigure(string? name, AppSettings options)
    {
        var watchedDir = Environment.GetEnvironmentVariable("WATCHED_DIR");
        if (!string.IsNullOrEmpty(watchedDir))
            options.WatchedDirectory = watchedDir;

        var duplicateDir = Environment.GetEnvironmentVariable("DUPLICATE_DIR");
        if (!string.IsNullOrEmpty(duplicateDir))
            options.DuplicateDirectory = duplicateDir;

        var configDirEnv = Environment.GetEnvironmentVariable("CONFIG_DIR");
        if (!string.IsNullOrEmpty(configDirEnv))
            options.ConfigDirectory = configDirEnv;

        var basePath = Environment.GetEnvironmentVariable("BASE_PATH");
        if (!string.IsNullOrEmpty(basePath))
            options.BasePath = basePath;

        var enableExternalSeriesMetadata = Environment.GetEnvironmentVariable("ENABLE_EXTERNAL_SERIES_METADATA");
        if (!string.IsNullOrEmpty(enableExternalSeriesMetadata))
            options.EnableExternalSeriesMetadata = enableExternalSeriesMetadata.Equals("true", StringComparison.OrdinalIgnoreCase);

        var comicVineApiKey = Environment.GetEnvironmentVariable("COMICVINE_API_KEY");
        if (!string.IsNullOrEmpty(comicVineApiKey))
            options.ComicVineApiKey = comicVineApiKey;

        var comicVineBaseUrl = Environment.GetEnvironmentVariable("COMICVINE_BASE_URL");
        if (!string.IsNullOrEmpty(comicVineBaseUrl))
            options.ComicVineBaseUrl = comicVineBaseUrl;

        var enableMangaDexMetadata = Environment.GetEnvironmentVariable("ENABLE_MANGADEX_METADATA");
        if (!string.IsNullOrEmpty(enableMangaDexMetadata))
            options.EnableMangaDexMetadata = enableMangaDexMetadata.Equals("true", StringComparison.OrdinalIgnoreCase);

        var mangaDexBaseUrl = Environment.GetEnvironmentVariable("MANGADEX_BASE_URL");
        if (!string.IsNullOrEmpty(mangaDexBaseUrl))
            options.MangaDexBaseUrl = mangaDexBaseUrl;

        var enableAniListMetadata = Environment.GetEnvironmentVariable("ENABLE_ANILIST_METADATA");
        if (!string.IsNullOrEmpty(enableAniListMetadata))
            options.EnableAniListMetadata = enableAniListMetadata.Equals("true", StringComparison.OrdinalIgnoreCase);

        var aniListBaseUrl = Environment.GetEnvironmentVariable("ANILIST_BASE_URL");
        if (!string.IsNullOrEmpty(aniListBaseUrl))
            options.AniListBaseUrl = aniListBaseUrl;

        var enableSuwayomi = Environment.GetEnvironmentVariable("ENABLE_SUWAYOMI");
        if (!string.IsNullOrEmpty(enableSuwayomi))
            options.EnableSuwayomi = enableSuwayomi.Equals("true", StringComparison.OrdinalIgnoreCase);

        var suwayomiBaseUrl = Environment.GetEnvironmentVariable("SUWAYOMI_BASE_URL");
        if (!string.IsNullOrEmpty(suwayomiBaseUrl))
            options.SuwayomiBaseUrl = suwayomiBaseUrl;

        var suwayomiUsername = Environment.GetEnvironmentVariable("SUWAYOMI_USERNAME");
        if (!string.IsNullOrEmpty(suwayomiUsername))
            options.SuwayomiUsername = suwayomiUsername;

        var suwayomiPassword = Environment.GetEnvironmentVariable("SUWAYOMI_PASSWORD");
        if (!string.IsNullOrEmpty(suwayomiPassword))
            options.SuwayomiPassword = suwayomiPassword;

        var suwayomiDownloadsDir = Environment.GetEnvironmentVariable("SUWAYOMI_DOWNLOADS_DIR");
        if (!string.IsNullOrEmpty(suwayomiDownloadsDir))
            options.SuwayomiDownloadsDirectory = suwayomiDownloadsDir;

        var suwayomiImportDir = Environment.GetEnvironmentVariable("SUWAYOMI_IMPORT_DIR");
        if (!string.IsNullOrEmpty(suwayomiImportDir))
            options.SuwayomiImportDirectory = suwayomiImportDir;

        var mangaDexRps = Environment.GetEnvironmentVariable("MANGADEX_REQUESTS_PER_SECOND");
        if (!string.IsNullOrEmpty(mangaDexRps) && int.TryParse(mangaDexRps, out var mdRps) && mdRps > 0)
            options.MangaDexRequestsPerSecond = mdRps;

        var aniListRpm = Environment.GetEnvironmentVariable("ANILIST_REQUESTS_PER_MINUTE");
        if (!string.IsNullOrEmpty(aniListRpm) && int.TryParse(aniListRpm, out var alRpm) && alRpm > 0)
            options.AniListRequestsPerMinute = alRpm;

        var downloadSeriesImages = Environment.GetEnvironmentVariable("DOWNLOAD_EXTERNAL_SERIES_IMAGES");
        if (!string.IsNullOrEmpty(downloadSeriesImages))
            options.DownloadExternalSeriesImages = downloadSeriesImages.Equals("true", StringComparison.OrdinalIgnoreCase);

        var seriesImageDir = Environment.GetEnvironmentVariable("SERIES_IMAGE_CACHE_DIR");
        if (!string.IsNullOrEmpty(seriesImageDir))
            options.SeriesImageCacheDirectory = seriesImageDir;

        var seriesImageMaxBytes = Environment.GetEnvironmentVariable("SERIES_IMAGE_MAX_BYTES");
        if (!string.IsNullOrEmpty(seriesImageMaxBytes) && int.TryParse(seriesImageMaxBytes, out var siMaxBytes) && siMaxBytes > 0)
            options.SeriesImageMaxBytes = siMaxBytes;

        var seriesImageMaxDownloadBytes = Environment.GetEnvironmentVariable("SERIES_IMAGE_MAX_DOWNLOAD_BYTES");
        if (!string.IsNullOrEmpty(seriesImageMaxDownloadBytes) && int.TryParse(seriesImageMaxDownloadBytes, out var siMaxDownloadBytes) && siMaxDownloadBytes > 0)
            options.SeriesImageMaxDownloadBytes = siMaxDownloadBytes;

        var seriesImageMaxDimension = Environment.GetEnvironmentVariable("SERIES_IMAGE_MAX_DIMENSION");
        if (!string.IsNullOrEmpty(seriesImageMaxDimension) && int.TryParse(seriesImageMaxDimension, out var siMaxDim) && siMaxDim > 0)
            options.SeriesImageMaxDimension = siMaxDim;

        var smtpHost = Environment.GetEnvironmentVariable("SMTP_HOST");
        if (!string.IsNullOrEmpty(smtpHost))
            options.SmtpHost = smtpHost;

        var smtpPort = Environment.GetEnvironmentVariable("SMTP_PORT");
        if (!string.IsNullOrEmpty(smtpPort) && int.TryParse(smtpPort, out var smtpPortValue) && smtpPortValue is > 0 and <= 65535)
            options.SmtpPort = smtpPortValue;

        var smtpUsername = Environment.GetEnvironmentVariable("SMTP_USERNAME");
        if (!string.IsNullOrEmpty(smtpUsername))
            options.SmtpUsername = smtpUsername;

        var smtpPassword = Environment.GetEnvironmentVariable("SMTP_PASSWORD");
        if (!string.IsNullOrEmpty(smtpPassword))
            options.SmtpPassword = smtpPassword;

        var smtpUseSsl = Environment.GetEnvironmentVariable("SMTP_USE_SSL");
        if (!string.IsNullOrEmpty(smtpUseSsl))
            options.SmtpUseSsl = smtpUseSsl.Equals("true", StringComparison.OrdinalIgnoreCase);

        var smtpAllowInsecure = Environment.GetEnvironmentVariable("SMTP_ALLOW_INSECURE");
        if (!string.IsNullOrEmpty(smtpAllowInsecure))
            options.SmtpAllowInsecure = smtpAllowInsecure.Equals("true", StringComparison.OrdinalIgnoreCase);

        var emailFromAddress = Environment.GetEnvironmentVariable("EMAIL_FROM_ADDRESS");
        if (!string.IsNullOrEmpty(emailFromAddress))
            options.EmailFromAddress = emailFromAddress;

        var emailFromName = Environment.GetEnvironmentVariable("EMAIL_FROM_NAME");
        if (!string.IsNullOrEmpty(emailFromName))
            options.EmailFromName = emailFromName;

        var emailMaxAttachmentMb = Environment.GetEnvironmentVariable("EMAIL_MAX_ATTACHMENT_MB");
        if (!string.IsNullOrEmpty(emailMaxAttachmentMb) && int.TryParse(emailMaxAttachmentMb, out var maxAttachmentMb) && maxAttachmentMb > 0)
            options.EmailMaxAttachmentMegabytes = maxAttachmentMb;

        // Automated error reporting. Provided as environment variables as well as
        // settings so the token can be supplied by a Docker secret / orchestrator
        // and never has to be typed into (or stored in) user-settings.json.
        var errorReportingEnabled = Environment.GetEnvironmentVariable("ERROR_REPORTING_ENABLED");
        if (!string.IsNullOrEmpty(errorReportingEnabled))
            options.ErrorReportingEnabled = errorReportingEnabled.Equals("true", StringComparison.OrdinalIgnoreCase);

        var errorReportingToken = Environment.GetEnvironmentVariable("ERROR_REPORTING_GITHUB_TOKEN");
        if (!string.IsNullOrEmpty(errorReportingToken))
            options.ErrorReportingGitHubToken = errorReportingToken;

        var errorReportingAssignee = Environment.GetEnvironmentVariable("ERROR_REPORTING_ASSIGNEE");
        if (!string.IsNullOrEmpty(errorReportingAssignee))
            options.ErrorReportingAssignee = errorReportingAssignee;

        var errorReportingMaxPerDay = Environment.GetEnvironmentVariable("ERROR_REPORTING_MAX_ISSUES_PER_DAY");
        if (!string.IsNullOrEmpty(errorReportingMaxPerDay) && int.TryParse(errorReportingMaxPerDay, out var maxIssuesPerDay) && maxIssuesPerDay >= 0)
            options.ErrorReportingMaxIssuesPerDay = maxIssuesPerDay;

        var errorReportingDedupeHours = Environment.GetEnvironmentVariable("ERROR_REPORTING_DEDUPE_WINDOW_HOURS");
        if (!string.IsNullOrEmpty(errorReportingDedupeHours) && int.TryParse(errorReportingDedupeHours, out var dedupeWindowHours) && dedupeWindowHours > 0)
            options.ErrorReportingDedupeWindowHours = dedupeWindowHours;

        var errorReportingComment = Environment.GetEnvironmentVariable("ERROR_REPORTING_COMMENT_ON_RECURRENCE");
        if (!string.IsNullOrEmpty(errorReportingComment))
            options.ErrorReportingCommentOnRecurrence = errorReportingComment.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
