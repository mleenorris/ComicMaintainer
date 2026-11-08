namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Background queue of changes to a series' on-disk cover artifacts: the <c>cover.&lt;ext&gt;</c>
/// sidecar in each series folder and the cover embedded in the first archive. The series metadata
/// cache publishes a change here and a separate consumer
/// (<see cref="ISeriesCoverArtifactWriter"/>) carries it out, so the cache does not depend on the
/// writers. They depend on the series library, which depends on the cache.
///
/// <para>The consumer handles one change at a time, which keeps repeated cover updates for a series
/// from racing each other. Enqueue calls return immediately and never throw; failures are logged
/// by the consumer.</para>
/// </summary>
public interface ISeriesCoverArtifactQueue
{
    /// <summary>
    /// Queue copying <paramref name="sourceFilePath"/> into the cover artifacts of the series
    /// identified by <paramref name="normalizedKey"/>.
    /// </summary>
    void EnqueueWrite(
        string normalizedKey,
        string sourceFilePath,
        string contentType,
        bool force = false);

    /// <summary>
    /// Queue removing the cover artifacts of the series identified by
    /// <paramref name="normalizedKey"/>.
    /// </summary>
    void EnqueueRemove(string normalizedKey);

    /// <summary>
    /// Start the background consumer. <paramref name="writer"/> carries out the queued changes;
    /// it is passed here rather than to the constructor because it depends (through the series
    /// library and the cache) on this queue. A call while the queue runs does nothing.
    /// </summary>
    Task StartAsync(ISeriesCoverArtifactWriter writer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop the background consumer and wait for the in-flight change (if any) to finish, so a
    /// graceful shutdown does not leave a half-written archive. Work queued after the stop is
    /// kept for the next <see cref="StartAsync"/>.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
