using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Enqueues per-file re-normalization in response to a change in the
/// preferred series-display language (per-series or global default). The
/// normalization step rewrites each file's ComicInfo.xml <c>&lt;Series&gt;</c>
/// element so the on-disk metadata stays in sync with the library's displayed
/// series name.
/// </summary>
public interface ISeriesLanguagePreferenceRetagService
{
    /// <summary>
    /// Queue every file whose series matches the given cache record (its
    /// canonical title, provider aliases, user aliases, or any of its
    /// localized titles) for forced normalization. Returns the id of the
    /// created normalization job, or <c>null</c> when nothing needs to be
    /// done (no matching files, normalization disabled, or required
    /// services are not wired up).
    /// </summary>
    Task<Guid?> QueueRetagForSeriesAsync(
        SeriesMetadataCacheRecord record,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queue every file whose series belongs to any cached record that does
    /// <i>not</i> have an explicit per-series language preference set. Used
    /// when the global default preferred language changes — only series
    /// without an override are affected.
    /// </summary>
    Task<Guid?> QueueRetagForGlobalDefaultAsync(CancellationToken cancellationToken = default);
}
