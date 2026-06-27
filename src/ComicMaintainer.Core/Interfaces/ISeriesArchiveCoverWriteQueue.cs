namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Background queue that defers embedding (and removing) the series cover image
/// inside the first comic archive of a series. Rewriting an archive on disk is
/// comparatively expensive, so these requests are processed asynchronously off
/// the caller's thread by a single background consumer.
///
/// <para>Work is serialized: at most one archive rewrite runs at a time, which
/// keeps repeated cover updates for the same series from racing each other (for
/// example colliding on the writer's sibling temp file). Enqueue calls return
/// immediately and never throw; the actual write is best-effort and any failure
/// is logged and swallowed by the underlying
/// <see cref="ISeriesArchiveCoverWriter"/>.</para>
/// </summary>
public interface ISeriesArchiveCoverWriteQueue
{
    /// <summary>
    /// Queue an embed of <paramref name="sourceFilePath"/> into the first comic
    /// archive of the series identified by <paramref name="normalizedKey"/>.
    /// </summary>
    void EnqueueWrite(
        string normalizedKey,
        string sourceFilePath,
        string contentType,
        bool force = false);

    /// <summary>
    /// Queue removal of any embedded cover from the first comic archive of the
    /// series identified by <paramref name="normalizedKey"/>.
    /// </summary>
    void EnqueueRemove(string normalizedKey);

    /// <summary>
    /// Start the background consumer that drains queued archive cover writes.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop accepting new work and wait for the in-flight item (if any) to
    /// finish so a graceful shutdown does not leave a half-written archive.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
