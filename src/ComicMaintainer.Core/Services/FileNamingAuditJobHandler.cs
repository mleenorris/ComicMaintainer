using System.Text.Json;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Scheduled-job handler that re-evaluates tracked comic files to ensure
/// they are properly named. By default the audit only reports how many
/// files appear to need renaming. When the <c>autoCorrect</c> option is
/// enabled the candidate files are queued through the normal
/// <see cref="IComicProcessorService.RenameFilesAsync(IEnumerable{string}, bool, CancellationToken)"/>
/// pipeline so the watcher's rename behavior is applied retroactively to
/// anything that slipped through (e.g. the watcher was disabled when the
/// file was added, or rename previously failed transiently).
/// </summary>
public class FileNamingAuditJobHandler : IScheduledJobHandler
{
    public const string Key = "file-naming-audit";

    // Poll the spawned rename batch job at this cadence while waiting for completion.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly ILogger<FileNamingAuditJobHandler> _logger;

    public FileNamingAuditJobHandler(
        IFileStoreService fileStore,
        IComicProcessorService processor,
        ILogger<FileNamingAuditJobHandler> logger)
    {
        _fileStore = fileStore;
        _processor = processor;
        _logger = logger;
    }

    public string JobKey => Key;
    public string DisplayName => "File Naming Audit";
    public string Description =>
        "Re-evaluates tracked comic files to ensure their filenames match the expected metadata-derived names. By default only files that haven't been renamed yet are considered; enable forceReprocess in options to re-check every file. The audit is report-only unless autoCorrect is enabled, in which case candidate files are queued through the rename pipeline.";

    // Default: disabled (so it doesn't run unprompted on existing installs) and daily when enabled.
    public ScheduledJobDefaults Defaults =>
        new(Enabled: false, IntervalMinutes: 60 * 24, OptionsJson: """{"forceReprocess":false,"autoCorrect":false}""");

    public async Task<string> ExecuteAsync(string? optionsJson, CancellationToken cancellationToken)
    {
        var options = ParseOptions(optionsJson);

        var allFiles = (await _fileStore.GetAllFilesAsync(cancellationToken)).ToList();
        var candidates = options.ForceReprocess
            ? allFiles.Select(f => f.FilePath).ToList()
            : allFiles.Where(f => !f.IsRenamed).Select(f => f.FilePath).ToList();

        _logger.LogInformation(
            "File naming audit starting: {Candidates}/{Total} file(s) to evaluate (forceReprocess={Force}, autoCorrect={AutoCorrect})",
            candidates.Count, allFiles.Count, options.ForceReprocess, options.AutoCorrect);

        if (candidates.Count == 0)
        {
            return $"Scanned {allFiles.Count}: all files already properly named.";
        }

        if (!options.AutoCorrect)
        {
            return $"Scanned {allFiles.Count}: {candidates.Count} file(s) appear to need renaming. Enable autoCorrect to rename.";
        }

        var jobId = await _processor.RenameFilesAsync(candidates, options.ForceReprocess, cancellationToken);

        // Poll until the spawned batch job reaches a terminal state so the scheduled-job's
        // last-run status reflects the actual outcome rather than the queue submission.
        ProcessingJob? job = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            job = _processor.GetJob(jobId);
            if (job is null)
            {
                // Job record was evicted before we observed completion; treat as success
                // since the rename pipeline only persists state on success.
                break;
            }
            if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
            {
                break;
            }
            await Task.Delay(PollInterval, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (job is null)
        {
            return $"Queued {candidates.Count} file(s) for rename evaluation; job record no longer available.";
        }

        return $"Evaluated {candidates.Count}/{allFiles.Count} file(s): {job.ProcessedFiles} processed, {job.FailedFiles} failed (status: {job.Status}).";
    }

    private static FileNamingAuditOptions ParseOptions(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
        {
            return new FileNamingAuditOptions();
        }
        try
        {
            return JsonSerializer.Deserialize<FileNamingAuditOptions>(
                       optionsJson,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new FileNamingAuditOptions();
        }
        catch
        {
            return new FileNamingAuditOptions();
        }
    }

    private sealed class FileNamingAuditOptions
    {
        public bool ForceReprocess { get; set; }
        public bool AutoCorrect { get; set; }
    }
}
