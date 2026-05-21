namespace ComicMaintainer.Core.Models;

/// <summary>
/// Status of a scheduled job's most recent run.
/// </summary>
public enum ScheduledJobStatus
{
    NeverRun,
    Running,
    Success,
    Failed,
    Skipped,
    Cancelled
}

/// <summary>
/// Persisted definition + last-run state for a recurring background job.
/// The <see cref="JobKey"/> is a stable string key (e.g. <c>metadata-audit</c>);
/// each registered <see cref="Interfaces.IScheduledJobHandler"/> claims one key.
/// </summary>
public class ScheduledJobEntity
{
    public int Id { get; set; }

    /// <summary>Stable string key matching an IScheduledJobHandler.JobKey.</summary>
    public string JobKey { get; set; } = string.Empty;

    /// <summary>Whether the periodic timer is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Recurrence interval in minutes (must be > 0 when Enabled).</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>Reserved for future cron-style scheduling; currently unused.</summary>
    public string? CronExpression { get; set; }

    public DateTime? LastRunUtc { get; set; }
    public DateTime? NextRunUtc { get; set; }
    public long? LastDurationMs { get; set; }
    public string LastStatus { get; set; } = ScheduledJobStatus.NeverRun.ToString();
    public string? LastMessage { get; set; }

    /// <summary>
    /// Optional handler-specific options serialized as JSON. Each handler decides
    /// the schema; e.g. metadata-audit stores <c>{"autoCorrect": false}</c>.
    /// </summary>
    public string? OptionsJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// View/API model for a scheduled job. Separate from the EF entity so the
/// controller layer doesn't leak persistence details and so handler-supplied
/// metadata (display name, description) can be merged in.
/// </summary>
public class ScheduledJobView
{
    public string JobKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public DateTime? NextRunUtc { get; set; }
    public long? LastDurationMs { get; set; }
    public ScheduledJobStatus LastStatus { get; set; }
    public string? LastMessage { get; set; }
    public string? OptionsJson { get; set; }
}

/// <summary>
/// Per-file finding produced by the <c>metadata-audit</c> scheduled job.
/// </summary>
public enum MetadataAuditFindingType
{
    /// <summary>The file's ComicInfo has both a series and an issue/chapter that match expectations.</summary>
    Ok,
    /// <summary>The file has no chapter/issue number set in its ComicInfo metadata.</summary>
    MissingChapter,
    /// <summary>The file's series tag does not match the expected resolved series name.</summary>
    SeriesMismatch,
    /// <summary>The file's metadata could not be read or extracted.</summary>
    Unreadable
}

public class MetadataAuditFindingEntity
{
    public int Id { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string FindingType { get; set; } = MetadataAuditFindingType.Ok.ToString();
    public string? ExpectedSeries { get; set; }
    public string? ActualSeries { get; set; }
    public string? ActualIssue { get; set; }
    public string? Details { get; set; }
    public DateTime DetectedAtUtc { get; set; } = DateTime.UtcNow;
}
