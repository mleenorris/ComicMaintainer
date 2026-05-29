using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Reacts to a change in the preferred series-display language (per-series or
/// global default) by flagging the affected files for a metadata backfill.
/// Rather than immediately running a forced normalization job, it bumps each
/// matching file's <c>MetadataVersion</c> so the scheduled metadata-backfill
/// job later rewrites each ComicInfo.xml <c>&lt;Series&gt;</c> element from the
/// DB-authoritative metadata. This keeps the database as the source of truth
/// and avoids the per-file churn (and UI flashing) of an eager normalize; the
/// on-disk files are allowed to be stale until the backfill job runs.
/// </summary>
public interface ISeriesLanguagePreferenceRetagService
{
    /// <summary>
    /// Flag every file whose series matches the given cache record (its
    /// canonical title, provider aliases, user aliases, or any of its
    /// localized titles) as needing a metadata backfill. Returns the number
    /// of files flagged, or <c>0</c> when nothing needs to be done (no
    /// matching files, normalization disabled, or required services are not
    /// wired up).
    /// </summary>
    Task<int> QueueRetagForSeriesAsync(
        SeriesMetadataCacheRecord record,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Flag every file whose series belongs to any cached record that does
    /// <i>not</i> have an explicit per-series language preference set as
    /// needing a metadata backfill. Used when the global default preferred
    /// language changes — only series without an override are affected.
    /// Returns the number of files flagged.
    /// </summary>
    Task<int> QueueRetagForGlobalDefaultAsync(CancellationToken cancellationToken = default);
}
