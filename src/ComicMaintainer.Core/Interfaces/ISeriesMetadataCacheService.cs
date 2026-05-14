using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Persisted cache of external series metadata + user-managed aliases.
/// </summary>
public interface ISeriesMetadataCacheService
{
    /// <summary>Normalizes a free-form title into the key used to look up cache entries.</summary>
    string NormalizeKey(string? title);

    /// <summary>Loads every cached record. Used by the library/grouping layer.</summary>
    Task<IReadOnlyList<SeriesMetadataCacheRecord>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads a single cache record by normalized key.</summary>
    Task<SeriesMetadataCacheRecord?> GetAsync(string normalizedKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the user-managed alias list (and optionally canonical title)
    /// for a series key. Returns the updated record.
    /// </summary>
    Task<SeriesMetadataCacheRecord> SetUserAliasesAsync(
        string seriesTitle,
        IEnumerable<string> userAliases,
        string? canonicalTitleOverride,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a single user alias by value (case-insensitive).</summary>
    Task<SeriesMetadataCacheRecord?> RemoveUserAliasAsync(
        string normalizedKey,
        string alias,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs an external lookup for <paramref name="seriesTitle"/> and upserts
    /// the result into the cache. Preserves any user-managed aliases / canonical
    /// title overrides.
    /// </summary>
    Task<SeriesMetadataCacheRecord> RefreshAsync(
        string seriesTitle,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replace the cached series image with a user-supplied upload. The image
    /// is marked as "user" so future external refreshes won't overwrite it.
    /// </summary>
    Task<SeriesMetadataCacheRecord> SetUserImageAsync(
        string seriesTitle,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear any cached series image (downloaded or user-uploaded). Returns
    /// null when no record exists for the key.
    /// </summary>
    Task<SeriesMetadataCacheRecord?> ClearImageAsync(
        string normalizedKey,
        CancellationToken cancellationToken = default);
}
