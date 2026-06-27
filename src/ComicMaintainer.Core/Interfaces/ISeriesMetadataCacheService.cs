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
    /// Loads a single cache record by any of its known titles (canonical
    /// title, user-selected name, provider/user aliases, or localized
    /// titles). Falls back to a scan when the exact normalized-key lookup
    /// misses, which happens after a manual match renames the series: the
    /// record stays keyed by the original folder title while the UI now
    /// refers to it by the matched canonical title. Returns null when no
    /// record matches.
    /// </summary>
    Task<SeriesMetadataCacheRecord?> GetByTitleAsync(string seriesTitle, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the user-managed alias list for a series key. Returns the
    /// updated record. Aliases form the candidate pool the user can pick the
    /// displayed series name from (see <see cref="SetSeriesNameAsync"/>).
    /// </summary>
    Task<SeriesMetadataCacheRecord> SetUserAliasesAsync(
        string seriesTitle,
        IEnumerable<string> userAliases,
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
    /// Persist the result of an automatic external-provider lookup into the
    /// cache so subsequent normalize/resolve operations read from the cache
    /// instead of re-hitting the provider. Mirrors
    /// <see cref="ApplyExternalMatchAsync"/> but records the lookup as an
    /// automatic <c>success</c> (rather than a user-driven <c>manual_match</c>),
    /// so the user can still pick a different candidate via the manual-match
    /// UI. Preserves user-canonical title overrides and user aliases; a
    /// user-uploaded image is never overwritten.
    /// <para>
    /// Callers should not invoke this when the cached record's
    /// <see cref="SeriesMetadataCacheRecord.LookupStatus"/> is <c>cleared</c>
    /// — a user-requested clear must not be silently undone by the next
    /// auto-lookup.
    /// </para>
    /// </summary>
    Task<SeriesMetadataCacheRecord> PersistExternalLookupAsync(
        string seriesTitle,
        ExternalSeriesMetadata lookup,
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
    /// Set (or clear, by passing null/empty) the user-selected series name —
    /// the single authoritative "pinned" name shown in the library and
    /// written into ComicInfo.xml's <c>&lt;Series&gt;</c>. It wins over the
    /// language-preference rule and stays sticky across refreshes until the
    /// user picks a different name or reverts to automatic (null/empty).
    /// <para>
    /// When non-null/empty: if the value matches (case-insensitive) the
    /// record's canonical title, a provider/user alias, or a localized title
    /// it is stored verbatim from that source; otherwise it is treated as a
    /// new custom name and added to the user-alias list. Passing null/empty
    /// reverts to automatic resolution.
    /// </para>
    /// <para>
    /// Returns null when no cache record exists for the key — a name can only
    /// be selected for a series the cache already knows about (no upsert).
    /// </para>
    /// </summary>
    Task<SeriesMetadataCacheRecord?> SetSeriesNameAsync(
        string seriesTitle,
        string? seriesName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes redundant "stale sibling" cache records: rows that carry no
    /// user-specific data, have a non-positive lookup status (<c>null</c> /
    /// <c>not_found</c> / <c>error</c>), and are subsumed by a strictly more
    /// authoritative record that already owns their key as one of its titles.
    /// These accumulate when a refresh issues a separate lookup per
    /// canonical/alias title. Returns the number of records removed.
    /// </summary>
    Task<int> CleanupStaleSiblingRecordsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Embed the cached cover image for the series identified by
    /// <paramref name="normalizedKey"/> into its first issue's archive (CBZ),
    /// as a page that sorts ahead of the real pages. Runs regardless of the
    /// <c>WriteCoverToFirstArchive</c> setting (it backs the explicit user
    /// action). Best-effort and idempotent. Returns true when the first
    /// issue's archive was (re)written; false when there is no cached image,
    /// no resolvable first issue, or the archive already carried the cover.
    /// </summary>
    Task<bool> EmbedCoverInFirstArchiveAsync(
        string normalizedKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Embed the cached cover image into the first issue's archive for every
    /// series that has a cached image. Best-effort and idempotent; returns the
    /// number of first issues whose archive was (re)written.
    /// </summary>
    Task<int> EmbedCoverInAllFirstArchivesAsync(CancellationToken cancellationToken = default);
}
