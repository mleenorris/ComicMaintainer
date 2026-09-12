using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Persists per-user UI preferences for the web interface.
/// </summary>
public interface IUserPreferencesService
{
    /// <summary>
    /// Returns the stored preferences for the given user. Preferences the user
    /// has never set are returned as <c>null</c> so callers can apply their own
    /// application-level defaults.
    /// </summary>
    Task<UserPreferences> GetPreferencesAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Merges the supplied preferences into the user's stored record. Only
    /// non-null properties on <paramref name="update"/> are written, so callers
    /// can send partial updates (e.g. theme only) without clobbering the rest.
    /// </summary>
    /// <returns>The full set of stored preferences after the merge.</returns>
    Task<UserPreferences> SavePreferencesAsync(UserPreferences update, CancellationToken cancellationToken = default);
}
