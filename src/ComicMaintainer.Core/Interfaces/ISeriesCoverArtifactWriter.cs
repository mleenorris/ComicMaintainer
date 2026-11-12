namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Carries out changes to a series' on-disk cover artifacts (the folder sidecars and the
/// first-archive embed). Consumes <see cref="ISeriesCoverArtifactQueue"/>, and re-applies a cover
/// on request. All operations are best-effort, as those of the underlying writers are.
/// </summary>
public interface ISeriesCoverArtifactWriter
{
    /// <summary>Copy the image at <paramref name="sourceFilePath"/> into the series' artifacts.</summary>
    Task WriteAsync(
        string normalizedKey,
        string sourceFilePath,
        string contentType,
        bool force = false,
        CancellationToken cancellationToken = default);

    /// <summary>Remove the series' artifacts.</summary>
    Task RemoveAsync(string normalizedKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-apply the cached cover of the series to its artifacts now, forcing the writers even
    /// when their automatic-write settings are off, and waiting for the first-archive embed
    /// (the slow part) so a caller driving a progress UI reports real per-series progress.
    /// Returns false when the series cannot be resolved or has no cached image to apply.
    /// </summary>
    Task<bool> ReapplyAsync(string seriesTitle, CancellationToken cancellationToken = default);
}
