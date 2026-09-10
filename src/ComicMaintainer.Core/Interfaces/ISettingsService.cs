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
    /// Update the global default preferred language used as a fallback when a
    /// series has no per-series language override. Accepts one of <c>en</c>,
    /// <c>ja</c>, <c>ko</c>, <c>zh</c>, or null/empty to clear.
    /// </summary>
    Task UpdateDefaultPreferredLanguageAsync(string? language, CancellationToken cancellationToken = default);
}
