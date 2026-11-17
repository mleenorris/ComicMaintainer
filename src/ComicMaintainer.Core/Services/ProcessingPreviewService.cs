using System.Collections.Concurrent;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Runs processing previews in <see cref="IBatchJobRegistry"/>, so they get the usual progress,
/// cancel and one-at-a-time limit on archive reads, and keeps their results in memory.
/// </summary>
/// <remarks>
/// Results are not stored in the database: a preview describes the library at one moment and is
/// worth little after a restart, and it is cheap to run again. The
/// <see cref="KeepMostRecent"/> newest previews are kept.
/// </remarks>
public sealed class ProcessingPreviewService : IProcessingPreviewService
{
    /// <summary>The operation name the preview's batch job is recorded under.</summary>
    public const string OperationName = "PreviewProcessing";

    /// <summary>How many previews are kept; starting another drops the oldest.</summary>
    internal const int KeepMostRecent = 5;

    private readonly IComicProcessorService _processor;
    private readonly IBatchJobRegistry _jobRegistry;
    private readonly ILogger<ProcessingPreviewService> _logger;

    // Previews by job id, and their ids oldest first.
    private readonly Dictionary<Guid, PreviewRun> _runs = new();
    private readonly Queue<Guid> _order = new();
    private readonly Lock _gate = new();

    public ProcessingPreviewService(
        IComicProcessorService processor,
        IBatchJobRegistry jobRegistry,
        ILogger<ProcessingPreviewService> logger)
    {
        _processor = processor;
        _jobRegistry = jobRegistry;
        _logger = logger;
    }

    public Guid StartPreview(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var files = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var run = new PreviewRun(files);

        // A file the preview cannot work out still counts as previewed: the reason is in its
        // result, and the job's failures are kept for the preview itself going wrong.
        var definition = new BatchJobDefinition(
            OperationName,
            "previewing processing of",
            async (filePath, token) =>
            {
                run.Add(await _processor.PreviewProcessingAsync(filePath, token));
                return true;
            },
            "Preview failed",
            (_, _) =>
            {
                run.Finish();
                return Task.CompletedTask;
            });

        var jobId = _jobRegistry.StartJob(definition, files);
        lock (_gate)
        {
            _runs[jobId] = run;
            _order.Enqueue(jobId);
            while (_order.Count > KeepMostRecent)
            {
                _runs.Remove(_order.Dequeue());
            }
        }

        _logger.LogInformation("Processing preview {JobId} started for {Count} file(s)", jobId, files.Count);
        return jobId;
    }

    public ProcessingPreviewResults? GetResults(Guid jobId, ProcessingPreviewFilter filter, int offset, int limit)
    {
        PreviewRun? run;
        lock (_gate)
        {
            _runs.TryGetValue(jobId, out run);
        }

        if (run is null)
        {
            return null;
        }

        var items = run.Snapshot();
        var summary = Summarize(items);
        var selected = items.Where(item => Selects(filter, item)).ToList();
        offset = Math.Clamp(offset, 0, selected.Count);
        limit = Math.Max(1, limit);
        var job = _jobRegistry.GetJobSummaries().FirstOrDefault(candidate => candidate.JobId == jobId);

        // A job the registry no longer remembers (deleted, or pruned) ended one way or the other.
        var status = job?.Status.ToString().ToLowerInvariant() ?? (run.Finished ? "completed" : "interrupted");
        return new ProcessingPreviewResults(
            jobId,
            status,
            run.TotalFiles,
            run.Finished,
            summary,
            filter,
            offset,
            limit,
            selected.Count,
            selected.Skip(offset).Take(limit).ToList());
    }

    private static bool Selects(ProcessingPreviewFilter filter, ProcessingPreviewItem item) => filter switch
    {
        ProcessingPreviewFilter.All => true,
        ProcessingPreviewFilter.Renamed => item.Renames,
        ProcessingPreviewFilter.Tags => item.TagChanges.Count > 0,
        ProcessingPreviewFilter.Clashes => item.Clash != ProcessingPreviewClash.None,
        ProcessingPreviewFilter.Errors => item.Error is not null,
        _ => item.HasChanges
    };

    private static ProcessingPreviewSummary Summarize(IReadOnlyList<ProcessingPreviewItem> items) => new(
        Checked: items.Count,
        Changed: items.Count(item => item.HasChanges),
        Renamed: items.Count(item => item.Renames),
        TagsChanged: items.Count(item => item.TagChanges.Count > 0),
        Clashes: items.Count(item => item.Clash != ProcessingPreviewClash.None),
        Unchanged: items.Count(item => !item.HasChanges),
        Errors: items.Count(item => item.Error is not null));

    /// <summary>
    /// Gives every file whose new name an earlier file of the preview also gets a
    /// <see cref="ProcessingPreviewClash.SameTargetInPreview"/> clash. Names are compared ignoring
    /// case, as <c>TargetFileExists</c> compares them with the files on disk. "Earlier" is the
    /// order the files were asked for in; the real job processes several files at once, so it
    /// may pick another of them, but one gets the name and the others are flagged as duplicates.
    /// </summary>
    internal static IReadOnlyList<ProcessingPreviewItem> MarkSharedTargets(
        IReadOnlyList<ProcessingPreviewItem> items,
        IReadOnlyDictionary<string, int> order)
    {
        var firstByTarget = new Dictionary<string, ProcessingPreviewItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(item => item.Renames).OrderBy(item => order.GetValueOrDefault(item.FilePath)))
        {
            firstByTarget.TryAdd(item.NewFilePath!, item);
        }

        return items
            .Select(item => item.Renames
                            && firstByTarget.TryGetValue(item.NewFilePath!, out var first)
                            && !ReferenceEquals(first, item)
                ? item with { Clash = ProcessingPreviewClash.SameTargetInPreview, ClashWith = first.FilePath }
                : item)
            .ToList();
    }

    /// <summary>The files of one preview and the results found so far.</summary>
    private sealed class PreviewRun
    {
        private readonly ConcurrentDictionary<string, ProcessingPreviewItem> _items = new(StringComparer.Ordinal);

        // Each file's place in the request, for the shared-target clashes.
        private readonly Dictionary<string, int> _order;
        private readonly Lock _snapshotGate = new();
        private IReadOnlyList<ProcessingPreviewItem>? _finishedSnapshot;
        private volatile bool _finished;

        public PreviewRun(IReadOnlyList<string> files)
        {
            TotalFiles = files.Count;
            _order = files.Select((file, index) => (file, index)).ToDictionary(entry => entry.file, entry => entry.index, StringComparer.Ordinal);
        }

        public int TotalFiles { get; }

        /// <summary>True once the job has checked every file.</summary>
        public bool Finished => _finished;

        public void Add(ProcessingPreviewItem item) => _items[item.FilePath] = item;

        public void Finish() => _finished = true;

        /// <summary>
        /// The results so far, with shared-target clashes marked, sorted by series then path. A
        /// finished preview's is worked out once.
        /// </summary>
        public IReadOnlyList<ProcessingPreviewItem> Snapshot()
        {
            lock (_snapshotGate)
            {
                if (_finishedSnapshot is not null)
                {
                    return _finishedSnapshot;
                }

                var finished = _finished;
                var snapshot = MarkSharedTargets(_items.Values.ToList(), _order)
                    .OrderBy(item => item.Series ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.FilePath, StringComparer.Ordinal)
                    .ToList();
                if (finished)
                {
                    _finishedSnapshot = snapshot;
                }

                return snapshot;
            }
        }
    }
}
