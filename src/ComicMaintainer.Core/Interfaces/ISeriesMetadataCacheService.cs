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
    /// Download a series cover image from an external provider URL and persist
    /// it under the cache key derived from <paramref name="seriesTitle"/>.
    /// Creates the cache record when missing. Throws
    /// <see cref="InvalidOperationException"/> when the current image is
    /// user-uploaded (caller should surface a 409); use
    /// <see cref="ClearImageAsync"/> first to drop the user image.
    /// </summary>
    /// <param name="seriesTitle">Free-form series title (will be normalized).</param>
    /// <param name="remoteImageUrl">Public http(s) URL returned by a provider.</param>
    /// <param name="source">Optional provider name (e.g. "ComicVine") for audit/source attribution.</param>
    Task<SeriesMetadataCacheRecord> ApplyExternalImageAsync(
        string seriesTitle,
        string remoteImageUrl,
        string? source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear any cached series image (downloaded or user-uploaded). Returns
    /// null when no record exists for the key.
    /// </summary>
    Task<SeriesMetadataCacheRecord?> ClearImageAsync(
        string normalizedKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Manually adopt an externally-supplied candidate as the cached
    /// metadata for the series. Used when the automatic best-match was wrong
    /// and the user picks a different candidate from the search results.
    /// Preserves the user's canonical-title override (if any) and the user
    /// alias list. A user-uploaded image is never overwritten; otherwise the
    /// candidate's image is downloaded best-effort and replaces the cached
    /// one. The cache row is upserted.
    /// </summary>
    /// <param name="seriesTitle">Free-form series title (will be normalized).</param>
    /// <param name="match">The candidate to adopt. Must have a non-empty CanonicalTitle.</param>
    Task<SeriesMetadataCacheRecord> ApplyExternalMatchAsync(
        string seriesTitle,
        ExternalSeriesMetadata match,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear the external-metadata fields cached for a series (provider
    /// aliases, source, last-lookup, lookup-status, and the canonical-title
    /// override if it came from the provider). User-managed aliases and a
    /// user-uploaded image are preserved; a provider-downloaded image is
    /// dropped. Returns null when no record exists for the key.
    /// </summary>
    Task<SeriesMetadataCacheRecord?> ClearExternalMetadataAsync(
        string normalizedKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Set (or clear) the user's preferred display language for a series.
    /// Accepts one of <c>en</c>, <c>ja</c>, <c>ko</c>, <c>zh</c>; passing
    /// null or empty clears the per-series preference so the global default
    /// applies. Throws <see cref="ArgumentException"/> for unsupported codes.
    /// Upserts the cache record when missing.
    /// </summary>
    Task<SeriesMetadataCacheRecord> SetPreferredLanguageAsync(
        string seriesTitle,
        string? language,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Set (or clear, by passing null/empty) the user-pinned localized
    /// title for a series. The pinned title wins over the language-preference
    /// rule but loses to <see cref="SeriesMetadataCacheRecord.IsUserCanonical"/>.
    /// <para>
    /// When non-null/empty the value must equal (case-insensitive,
    /// whitespace-trimmed) either the record's
    /// <see cref="SeriesMetadataCacheRecord.CanonicalTitle"/> or one of its
    /// <see cref="SeriesMetadataCacheRecord.LocalizedTitles"/> entries; an
    /// <see cref="ArgumentException"/> is thrown otherwise. The persisted
    /// value is the canonical-cased version of the title from the record
    /// so the pin survives whitespace/case-only differences in user input.
    /// </para>
    /// <para>
    /// Returns null when no cache record exists for the key — pins can
    /// only be set on series the cache already knows about (no upsert).
    /// </para>
    /// </summary>
    Task<SeriesMetadataCacheRecord?> SetPinnedLocalizedTitleAsync(
        string seriesTitle,
        string? pinnedTitle,
        CancellationToken cancellationToken = default);
}
