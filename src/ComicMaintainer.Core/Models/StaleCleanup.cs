namespace ComicMaintainer.Core.Models;

/// <summary>
/// How a stale-entry cleanup was started, which decides how cautious it is.
/// </summary>
/// <param name="IsManual">
/// True for a cleanup an administrator started (the settings or files endpoint). A manual
/// run deletes rows for files that are missing right now instead of waiting for a second
/// run after <c>AppSettings.StaleCleanupMissingGraceHours</c>.
/// </param>
/// <param name="Confirmed">
/// True when the administrator confirmed a cleanup that the guards stopped because the
/// watched directory held no comics or because it would delete more than
/// <c>AppSettings.StaleCleanupMaxRemovalPercent</c> of the library. Ignored for scheduled
/// runs, and never overrides a missing watched directory or sentinel file.
/// </param>
public sealed record StaleCleanupOptions(bool IsManual = false, bool Confirmed = false)
{
    /// <summary>A scheduled (startup, timer or library-scan) run.</summary>
    public static StaleCleanupOptions Scheduled { get; } = new();

    /// <summary>A manual run that has not been confirmed.</summary>
    public static StaleCleanupOptions Manual { get; } = new(IsManual: true);
}

/// <summary>
/// Outcome of a stale-entry cleanup.
/// </summary>
public enum StaleCleanupStatus
{
    /// <summary>The run checked every row; <see cref="StaleCleanupResult.RemovedCount"/> were deleted.</summary>
    Completed,

    /// <summary>The watched directory does not exist or cannot be read. Nothing was changed.</summary>
    WatchedDirectoryUnavailable,

    /// <summary>The configured sentinel file is missing, so the library is presumed unmounted. Nothing was changed.</summary>
    SentinelMissing,

    /// <summary>
    /// The watched directory contains no comic archives although files are tracked, which
    /// is what an unmounted share looks like. Nothing was changed; a confirmed manual run
    /// may proceed.
    /// </summary>
    WatchedDirectoryEmpty,

    /// <summary>
    /// The run would have deleted more than the configured share of the library. Missing
    /// files were stamped but nothing was deleted; a confirmed manual run may proceed.
    /// </summary>
    ThresholdExceeded,

    /// <summary>The run failed (for example the database was unavailable). Nothing was deleted.</summary>
    Failed,

    /// <summary>The run was cancelled before it deleted anything.</summary>
    Cancelled
}

/// <summary>
/// What a stale-entry cleanup found and did.
/// </summary>
public sealed record StaleCleanupResult
{
    public StaleCleanupStatus Status { get; init; }

    /// <summary>Rows deleted by this run.</summary>
    public int RemovedCount { get; init; }

    /// <summary>Rows tracked when the run started.</summary>
    public int TrackedCount { get; init; }

    /// <summary>Rows whose file could not be found on disk during this run.</summary>
    public int MissingCount { get; init; }

    /// <summary>Rows this run would delete (missing for long enough), before the share guard.</summary>
    public int RemovableCount { get; init; }

    /// <summary>Rows first seen missing by this run; they are deleted by a later run if still missing.</summary>
    public int NewlyMissingCount { get; init; }

    /// <summary>Rows whose file was missing before and has reappeared.</summary>
    public int RestoredCount { get; init; }

    /// <summary>Rows that could not be checked because of an I/O or permission error; they are left alone.</summary>
    public int UncheckedCount { get; init; }

    /// <summary>The share guard in force for this run (percent).</summary>
    public int MaxRemovalPercent { get; init; }

    /// <summary>A sentence describing the outcome, suitable for logs and the UI.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>True when an administrator may repeat the run with <see cref="StaleCleanupOptions.Confirmed"/>.</summary>
    public bool RequiresConfirmation =>
        Status is StaleCleanupStatus.WatchedDirectoryEmpty or StaleCleanupStatus.ThresholdExceeded;
}
