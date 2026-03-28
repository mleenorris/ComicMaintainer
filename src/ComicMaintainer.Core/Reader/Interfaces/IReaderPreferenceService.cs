using ComicMaintainer.Core.Reader.Models;

namespace ComicMaintainer.Core.Reader.Interfaces;

/// <summary>
/// Manages per-user reader preferences.
/// </summary>
public interface IReaderPreferenceService
{
    /// <summary>
    /// Returns the preferences for the given user.
    /// Returns defaults if no preferences have been saved yet.
    /// </summary>
    Task<ReaderPreferences> GetPreferencesAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves (creates or updates) preferences for the given user.
    /// </summary>
    Task SavePreferencesAsync(ReaderPreferences preferences, CancellationToken cancellationToken = default);
}
