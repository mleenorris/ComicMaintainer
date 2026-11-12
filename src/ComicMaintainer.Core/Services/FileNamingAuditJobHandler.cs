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
public class FileNamingAuditJobHandler : IScheduledJobFindingsHandler
{
    public const string Key = "file-naming-audit";

    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly IBatchJobRegistry _jobRegistry;
    private readonly ILogger<FileNamingAuditJobHandler> _logger;

    public FileNamingAuditJobHandler(
        IFileStoreService fileStore,
        IComicProcessorService processor,
        IBatchJobRegistry jobRegistry,
        ILogger<FileNamingAuditJobHandler> logger)
    {
        _fileStore = fileStore;
        _processor = processor;
        _jobRegistry = jobRegistry;
        _logger = logger;
    }

    public string JobKey => Key;
    public string DisplayName => "File Naming Audit";
    public string Description =>
        "Re-evaluates tracked comic files to ensure their filenames match the expected metadata-derived names. By default only files that haven't been renamed yet are considered; enable forceReprocess in options to re-check every file. The audit is report-only unless autoCorrect is enabled, in which case candidate files are queued through the rename pipeline.";

    // Default: disabled (so it doesn't run unprompted on existing installs) and daily when enabled.
    public ScheduledJobDefaults Defaults =>
        new(Enabled: false, IntervalMinutes: 60 * 24, OptionsJson: """{"forceReprocess":false,"autoCorrect":false}""");

    public async Task<string> ExecuteAsync(string? optionsJson, CancellationToken cancellationToken) =>
        (await ExecuteWithFindingsAsync(optionsJson, cancellationToken)).Summary;

    public async Task<ScheduledJobResult> ExecuteWithFindingsAsync(string? optionsJson, CancellationToken cancellationToken)
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
            return new ScheduledJobResult($"Scanned {allFiles.Count}: all files already properly named.");
        }

        if (!options.AutoCorrect)
        {
            return new ScheduledJobResult(
                $"Scanned {allFiles.Count}: {candidates.Count} file(s) appear to need renaming. Enable autoCorrect to rename.",
                candidates.Count);
        }

        var jobId = await _processor.RenameFilesAsync(candidates, options.ForceReprocess, cancellationToken);

        // Wait until the spawned batch job finishes so the scheduled job's last-run status
        // reflects the actual outcome rather than the queue submission.
        var job = await _jobRegistry.WaitForCompletionAsync(jobId, cancellationToken);

        if (job is null)
        {
            return new ScheduledJobResult($"Queued {candidates.Count} file(s) for rename evaluation; job record no longer available.");
        }

        // What autoCorrect renamed is done with; only what it could not rename is worth a look.
        return new ScheduledJobResult(
            $"Evaluated {candidates.Count}/{allFiles.Count} file(s): {job.ProcessedFiles} processed, {job.FailedFiles} failed (status: {job.Status}).",
            job.FailedFiles);
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
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
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
