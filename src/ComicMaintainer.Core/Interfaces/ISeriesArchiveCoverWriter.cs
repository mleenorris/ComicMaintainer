namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Embeds (and removes) a copy of a series cover image inside the
/// <em>first</em> comic archive of that series, as a <c>cover.&lt;ext&gt;</c>
/// entry, so readers that derive the series cover from the first issue's
/// archive contents (rather than a sidecar file) see the same cover that
/// ComicMaintainer's UI shows.
///
/// Only writable CBZ (ZIP) archives are modified; CBR archives are left
/// untouched. All operations are best-effort: failures are logged and
/// swallowed so a disk-write problem (read-only mount, permission denied,
/// full disk, corrupt archive) never causes the upstream metadata update to
/// fail. Writes are idempotent: an archive that already contains a
/// byte-identical cover is left untouched.
/// </summary>
public interface ISeriesArchiveCoverWriter
{
    /// <summary>
    /// Embed <paramref name="sourceFilePath"/> into the first comic archive of
    /// the series identified by <paramref name="normalizedKey"/> as a
    /// <c>cover.&lt;ext&gt;</c> entry, replacing any existing managed
    /// <c>cover.*</c> entry. No-op when the feature is disabled in settings,
    /// when the series has no on-disk files, or when the first archive is not
    /// a writable CBZ.
    /// </summary>
    Task WriteAsync(
        string normalizedKey,
        string sourceFilePath,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove any <c>cover.*</c> entry written by <see cref="WriteAsync"/> from
    /// the first comic archive of the series identified by
    /// <paramref name="normalizedKey"/>.
    /// </summary>
    Task RemoveAsync(
        string normalizedKey,
        CancellationToken cancellationToken = default);
}
