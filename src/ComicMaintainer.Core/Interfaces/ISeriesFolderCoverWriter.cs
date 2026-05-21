namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Writes (and removes) a copy of a series cover image inside each on-disk
/// folder that contains files for that series, so external readers
/// (Komga / Kavita / Calibre / file managers) see the same cover that
/// ComicMaintainer's UI shows. The on-disk copy is named
/// <c>cover.&lt;ext&gt;</c> where <c>&lt;ext&gt;</c> matches the source
/// image's content type (<c>.jpg</c>, <c>.png</c>, or <c>.webp</c>).
///
/// All operations are best-effort: failures are logged and swallowed so a
/// disk-write problem (read-only mount, permission denied, full disk) never
/// causes the upstream metadata update to fail.
/// </summary>
public interface ISeriesFolderCoverWriter
{
    /// <summary>
    /// Copy <paramref name="sourceFilePath"/> into every on-disk folder that
    /// contains files for the series identified by <paramref name="normalizedKey"/>.
    /// Existing <c>cover.*</c> files in the target folders that have a
    /// different extension are removed so only one canonical cover remains
    /// per folder. No-op when the feature is disabled in settings or when
    /// the series has no on-disk folders.
    /// </summary>
    Task WriteAsync(
        string normalizedKey,
        string sourceFilePath,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove any <c>cover.*</c> file written by <see cref="WriteAsync"/>
    /// from every on-disk folder that contains files for the series
    /// identified by <paramref name="normalizedKey"/>.
    /// </summary>
    Task RemoveAsync(
        string normalizedKey,
        CancellationToken cancellationToken = default);
}
