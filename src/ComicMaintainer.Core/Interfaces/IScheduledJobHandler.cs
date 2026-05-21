using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Implemented by services that participate in the scheduled-jobs framework.
/// Each handler claims a stable <see cref="JobKey"/> and exposes
/// <see cref="ExecuteAsync"/> as the unit of work to run when the schedule
/// fires (or when "Run Now" is invoked).
/// </summary>
public interface IScheduledJobHandler
{
    /// <summary>Stable identifier (e.g. <c>metadata-audit</c>). Must be unique.</summary>
    string JobKey { get; }

    /// <summary>Human-friendly name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>Short description shown beneath the name in the UI.</summary>
    string Description { get; }

    /// <summary>
    /// Default schedule applied when the framework first registers this handler
    /// (typically on first startup, before the user has saved any preferences).
    /// </summary>
    ScheduledJobDefaults Defaults { get; }

    /// <summary>
    /// Run a single occurrence of the job. The implementation is responsible
    /// for honoring <paramref name="cancellationToken"/> and for not running
    /// concurrent occurrences (the host already enforces single-flight per key).
    /// </summary>
    /// <param name="optionsJson">
    /// The handler-specific options blob currently saved on the job's
    /// <see cref="ScheduledJobEntity"/>. May be null/empty.
    /// </param>
    /// <returns>
    /// A short status message to surface in the UI as the last-run summary.
    /// Throw to mark the run as failed.
    /// </returns>
    Task<string> ExecuteAsync(string? optionsJson, CancellationToken cancellationToken);
}

/// <summary>
/// Defaults supplied by an <see cref="IScheduledJobHandler"/> when its row is
/// first created in the <c>ScheduledJobs</c> table.
/// </summary>
public sealed record ScheduledJobDefaults(bool Enabled, int IntervalMinutes, string? OptionsJson = null);
