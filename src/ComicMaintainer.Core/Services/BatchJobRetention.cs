namespace ComicMaintainer.Core.Services;

/// <summary>
/// How long finished batch jobs are remembered, in memory and in the database. A terminal job
/// is forgotten once it is older than <see cref="MaxAge"/> and is not among the
/// <see cref="KeepMostRecent"/> newest terminal jobs. Jobs still in flight are never forgotten.
/// </summary>
public static class BatchJobRetention
{
    /// <summary>Terminal jobs older than this may be forgotten.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>Terminal jobs kept regardless of age, so recent history is never empty.</summary>
    public const int KeepMostRecent = 50;
}
