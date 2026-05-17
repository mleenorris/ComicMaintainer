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
    Task UpdateExternalSeriesMetadataEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    Task UpdateComicVineApiKeyAsync(string? apiKey, CancellationToken cancellationToken = default);
    Task UpdateComicVineBaseUrlAsync(string? baseUrl, CancellationToken cancellationToken = default);
    Task UpdateMangaDexEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    Task UpdateMangaDexBaseUrlAsync(string? baseUrl, CancellationToken cancellationToken = default);
    Task UpdateAniListEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    Task UpdateAniListBaseUrlAsync(string? baseUrl, CancellationToken cancellationToken = default);
}
