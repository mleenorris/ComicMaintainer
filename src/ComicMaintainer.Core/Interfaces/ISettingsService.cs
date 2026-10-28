using ComicMaintainer.Core.Configuration;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Service for persisting and managing application settings
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// Get the current settings
    /// </summary>
    Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the log max bytes setting
    /// </summary>
    Task UpdateLogMaxBytesAsync(int maxBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the filename format setting
    /// </summary>
    Task UpdateFilenameFormatAsync(string format, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the issue number padding setting
    /// </summary>
    Task UpdateIssueNumberPaddingAsync(int padding, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the watcher enable rename setting
    /// </summary>
    Task UpdateWatcherEnableRenameAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the watcher enable normalize setting
    /// </summary>
    Task UpdateWatcherEnableNormalizeAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update whether the cached series cover image is embedded into the first
    /// issue's archive as a <c>cover.&lt;ext&gt;</c> entry.
    /// </summary>
    Task UpdateWriteCoverToFirstArchiveAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update whether the cached series cover image is also written into each
    /// on-disk series folder as a <c>cover.&lt;ext&gt;</c> sidecar file.
    /// </summary>
    Task UpdateWriteCoverToSeriesFolderAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update whether external metadata refreshes also download a series cover
    /// image from the chosen provider.
    /// </summary>
    Task UpdateDownloadExternalSeriesImagesAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the maximum size (in bytes) of a stored series cover image.
    /// </summary>
    Task UpdateSeriesImageMaxBytesAsync(int maxBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the hard ceiling (in bytes) for the raw payload fetched when
    /// downloading an external series cover image.
    /// </summary>
    Task UpdateSeriesImageMaxDownloadBytesAsync(int maxDownloadBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the maximum width or height (in pixels) of a stored series cover image.
    /// </summary>
    Task UpdateSeriesImageMaxDimensionAsync(int maxDimension, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist all three series cover image limits in one write, so they are
    /// saved together or not at all. Every limit must be greater than 0, and
    /// <paramref name="maxDownloadBytes"/> must be at least <paramref name="maxBytes"/>.
    /// </summary>
    Task UpdateSeriesImageLimitsAsync(
        int maxBytes,
        int maxDownloadBytes,
        int maxDimension,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the GitHub token setting
    /// </summary>
    Task UpdateGitHubTokenAsync(string? token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the GitHub repository setting
    /// </summary>
    Task UpdateGitHubRepositoryAsync(string? repository, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the GitHub issue assignee setting
    /// </summary>
    Task UpdateGitHubIssueAssigneeAsync(string? assignee, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the database cleanup interval setting (in hours, 0 = only on startup)
    /// </summary>
    Task UpdateDatabaseCleanupIntervalHoursAsync(int hours, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the default library view shown on initial page load ("files" or "series").
    /// </summary>
    Task UpdateDefaultLibraryViewAsync(string view, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enables or disables anonymous self-service account registration.
    /// </summary>
    Task UpdateAllowRegistrationAsync(bool allowed, CancellationToken cancellationToken = default);
    Task UpdateExternalSeriesMetadataEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    Task UpdateComicVineApiKeyAsync(string? apiKey, CancellationToken cancellationToken = default);
    Task UpdateComicVineBaseUrlAsync(string? baseUrl, CancellationToken cancellationToken = default);
    Task UpdateMangaDexEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    Task UpdateMangaDexBaseUrlAsync(string? baseUrl, CancellationToken cancellationToken = default);
    Task UpdateAniListEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    Task UpdateAniListBaseUrlAsync(string? baseUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist the external series metadata settings in one write, so they are
    /// saved together or not at all. A null <paramref name="comicVineApiKey"/>
    /// leaves the stored key untouched so the UI never has to round-trip the
    /// secret; pass an empty or blank string to clear it. A blank base URL
    /// leaves the stored URL untouched. Values are trimmed.
    /// </summary>
    Task UpdateExternalSeriesMetadataSettingsAsync(
        bool enabled,
        string? comicVineApiKey,
        string? comicVineBaseUrl,
        bool enableMangaDex,
        string? mangaDexBaseUrl,
        bool enableAniList,
        string? aniListBaseUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist the Suwayomi download-server settings. A null
    /// <paramref name="password"/> leaves the stored password untouched so the
    /// UI never has to round-trip the secret; pass an empty string to clear it.
    /// <paramref name="downloadsDirectory"/> and <paramref name="importDirectory"/>
    /// configure the Suwayomi download import and follow the same rule: null
    /// keeps the stored value, an empty string clears it.
    /// </summary>
    /// <exception cref="ArgumentException">A value is invalid; the message says why.</exception>
    Task UpdateSuwayomiSettingsAsync(
        bool enabled,
        string? baseUrl,
        string? username,
        string? password,
        string? downloadsDirectory = null,
        string? importDirectory = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the global default preferred language used as a fallback when a
    /// series has no per-series language override. Accepts one of <c>en</c>,
    /// <c>ja</c>, <c>ko</c>, <c>zh</c>, or null/empty to clear.
    /// </summary>
    Task UpdateDefaultPreferredLanguageAsync(string? language, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist the SMTP / email-delivery settings used to send comics to saved
    /// ereader devices. A null <paramref name="smtpPassword"/> leaves the stored
    /// password untouched so the UI never has to round-trip the secret; pass an
    /// empty string to clear it.
    /// </summary>
    Task UpdateEmailSettingsAsync(
        string? smtpHost,
        int smtpPort,
        string? smtpUsername,
        string? smtpPassword,
        bool smtpUseSsl,
        bool smtpAllowInsecure,
        string? fromAddress,
        string? fromName,
        int maxAttachmentMegabytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist the email rate limit. Each value is a non-negative integer and
    /// 0 disables that limit. Comic deliveries over a limit stay pending until
    /// a slot frees up.
    /// </summary>
    /// <param name="maxSendsPerHour">Most comic emails in any rolling 60-minute window.</param>
    /// <param name="maxSendsPerDay">Most comic emails in any rolling 24-hour window.</param>
    /// <param name="minSecondsBetweenSends">Minimum gap between two comic emails.</param>
    /// <exception cref="ArgumentException">A value is negative or above its upper bound.</exception>
    Task UpdateEmailRateLimitSettingsAsync(
        int maxSendsPerHour,
        int maxSendsPerDay,
        int minSecondsBetweenSends,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist the automated error-reporting settings. A null
    /// <paramref name="gitHubToken"/> leaves the stored token untouched so the
    /// UI never has to round-trip the secret; pass an empty string to clear it.
    /// The destination repository is fixed and is not a setting.
    /// </summary>
    Task UpdateErrorReportingSettingsAsync(
        bool enabled,
        string? gitHubToken,
        string? assignee,
        int maxIssuesPerDay,
        int dedupeWindowHours,
        bool commentOnRecurrence,
        CancellationToken cancellationToken = default);
}
