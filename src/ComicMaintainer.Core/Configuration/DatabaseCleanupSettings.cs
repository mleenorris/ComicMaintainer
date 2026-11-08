namespace ComicMaintainer.Core.Configuration;

/// <summary>
/// The database-cleanup settings saved together from the Settings page (see
/// <c>ISettingsService.UpdateDatabaseCleanupSettingsAsync</c>). Each value maps to the
/// <see cref="AppSettings"/> property of the same meaning.
/// </summary>
/// <param name="IntervalHours">
/// <see cref="AppSettings.DatabaseCleanupIntervalHours"/>: hours between scheduled cleanups,
/// 0 or more (0 runs it only once after start-up).
/// </param>
/// <param name="StaleCleanupMaxRemovalPercent">
/// <see cref="AppSettings.StaleCleanupMaxRemovalPercent"/>: 0 to
/// <see cref="AppSettings.MaxStaleCleanupRemovalPercent"/>.
/// </param>
/// <param name="StaleCleanupMissingGraceHours">
/// <see cref="AppSettings.StaleCleanupMissingGraceHours"/>: 0 to
/// <see cref="AppSettings.MaxStaleCleanupMissingGraceHours"/>.
/// </param>
/// <param name="StaleCleanupSentinelFile">
/// <see cref="AppSettings.StaleCleanupSentinelFile"/>: null or blank disables the check;
/// otherwise a path of at most <see cref="MaxSentinelFileLength"/> characters.
/// </param>
/// <param name="ProcessingHistoryRetentionDays">
/// <see cref="AppSettings.ProcessingHistoryRetentionDays"/>: 0 (keep regardless of age) to
/// <see cref="AppSettings.MaxProcessingHistoryRetentionDays"/>.
/// </param>
/// <param name="ProcessingHistoryMaxEntries">
/// <see cref="AppSettings.ProcessingHistoryMaxEntries"/>: 0 (no cap) to
/// <see cref="AppSettings.MaxProcessingHistoryEntries"/>.
/// </param>
public sealed record DatabaseCleanupSettings(
    int IntervalHours,
    int StaleCleanupMaxRemovalPercent,
    int StaleCleanupMissingGraceHours,
    string? StaleCleanupSentinelFile,
    int ProcessingHistoryRetentionDays,
    int ProcessingHistoryMaxEntries)
{
    /// <summary>Longest accepted <see cref="StaleCleanupSentinelFile"/>.</summary>
    public const int MaxSentinelFileLength = 1024;
}
