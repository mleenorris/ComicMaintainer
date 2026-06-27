namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Embeds (and removes) a copy of a series cover image inside the <em>first
/// issue's</em> on-disk archive (CBZ) for that series, as a page that sorts
/// first so readers display it as the series cover. The embedded page is
/// named with a recognizable sentinel (<c>0000-cmcover.&lt;ext&gt;</c>) so it
/// can be replaced or removed idempotently without touching the real pages.
///
/// All operations are best-effort: failures are logged and swallowed so a
/// disk-write problem (read-only mount, permission denied, corrupt archive,
/// non-writable CBR) never causes the upstream metadata update to fail. Only
/// writable CBZ archives are modified; CBR first issues are skipped.
/// </summary>
public interface ISeriesArchiveCoverWriter
{
    /// <summary>
    /// Embed <paramref name="sourceFilePath"/> into the first issue's archive
    /// for the series identified by <paramref name="normalizedKey"/>. Any
    /// previously embedded sentinel cover page is replaced. The operation is
    /// idempotent: when the first issue already carries an identical embedded
    /// cover the archive is left untouched.
    /// </summary>
    /// <param name="normalizedKey">The series' image cache key.</param>
    /// <param name="sourceFilePath">Absolute path to the cover image bytes.</param>
    /// <param name="contentType">Cover image content type (jpeg/png/webp).</param>
    /// <param name="force">
    /// When false (the default) the write only runs if the
    /// <c>WriteCoverToFirstArchive</c> setting is enabled. When true the write
    /// runs regardless of the setting (used by the explicit user action).
    /// </param>
    /// <returns>True when the first issue's archive was (re)written.</returns>
    Task<bool> WriteAsync(
        string normalizedKey,
        string sourceFilePath,
        string contentType,
        bool force = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove any sentinel cover page previously embedded by
    /// <see cref="WriteAsync"/> from the first issue's archive for the series
    /// identified by <paramref name="normalizedKey"/>.
    /// </summary>
    Task RemoveAsync(
        string normalizedKey,
        CancellationToken cancellationToken = default);
}
