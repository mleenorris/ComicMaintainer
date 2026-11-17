using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// Previews processing: what Process (normalize, then rename, as a forced reprocess) would do to
/// files, without changing anything. One file is answered at once; a list, a series or the
/// whole library is previewed in a background job whose results are read in pages. Nothing is
/// written until the user processes the files through the usual processing routes.
/// </summary>
[ApiController]
[Route("api/files/process-preview")]
[Authorize]
public class ProcessPreviewController : ControllerBase
{
    /// <summary>The default and largest page of results.</summary>
    private const int DefaultLimit = 200;
    private const int MaxLimit = 1000;

    private readonly IComicProcessorService _processor;
    private readonly IProcessingPreviewService _previews;
    private readonly IFileStoreService _fileStore;
    private readonly ISeriesLibraryService _seriesLibrary;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<ProcessPreviewController> _logger;

    public ProcessPreviewController(
        IComicProcessorService processor,
        IProcessingPreviewService previews,
        IFileStoreService fileStore,
        ISeriesLibraryService seriesLibrary,
        IOptionsMonitor<AppSettings> settings,
        ILogger<ProcessPreviewController> logger)
    {
        _processor = processor;
        _previews = previews;
        _fileStore = fileStore;
        _seriesLibrary = seriesLibrary;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>What processing would do to one file, worked out in the request.</summary>
    [HttpGet]
    [FailureMessage("Error previewing processing")]
    public async Task<ActionResult<object>> PreviewFile([FromQuery] string? filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return BadRequest(new { error = "File path is required" });
        }

        if (!Roots().TryResolveWithin(filePath, out _))
        {
            _logger.LogWarning("Preview requested for a file outside the allowed directories: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return BadRequest(new { error = "File path is outside the allowed directory" });
        }

        var item = await _processor.PreviewProcessingAsync(filePath, cancellationToken);
        return Ok(ToResponse(item));
    }

    /// <summary>
    /// Starts a preview job over the files a scope selects: <c>all</c> (every file that is not
    /// flagged as a duplicate, as <c>process-all</c> takes them), <c>selected</c> (the files named)
    /// or <c>series</c> (the issues of one series that are not flagged as duplicates).
    /// </summary>
    [HttpPost]
    [FailureMessage("Error starting the preview")]
    public async Task<ActionResult<JobsController.BatchJobResponse>> StartPreview(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] PreviewRequest? request,
        CancellationToken cancellationToken)
    {
        request ??= new PreviewRequest();
        List<string> files;
        switch (request.Scope?.Trim().ToLowerInvariant())
        {
            case "all":
                var allFiles = await _fileStore.GetAllFilesAsync(cancellationToken);
                files = allFiles.Where(file => !file.IsDuplicate).Select(file => file.FilePath).ToList();
                break;

            case "selected":
                files = (request.Files ?? new List<string>()).Where(file => !string.IsNullOrWhiteSpace(file)).ToList();
                if (files.Count == 0)
                {
                    return BadRequest(new { error = "No files specified" });
                }

                var roots = Roots();
                if (files.FirstOrDefault(file => !roots.TryResolveWithin(file, out _)) is { } outside)
                {
                    _logger.LogWarning("Preview requested for a file outside the allowed directories: {FilePath}", LoggingHelper.SanitizePathForLog(outside));
                    return BadRequest(new { error = JobsController.OutsideAllowedDirectoryError });
                }

                break;

            case "series":
                if (string.IsNullOrWhiteSpace(request.SeriesId))
                {
                    return BadRequest(new { error = "seriesId is required" });
                }

                var series = await _seriesLibrary.GetSeriesIssuesAsync(request.SeriesId, filter: null, page: 1, perPage: -1, cancellationToken);
                if (series is null)
                {
                    return NotFound(new { error = "Series not found" });
                }

                files = series.Issues
                    .Where(issue => !issue.Duplicate && !string.IsNullOrWhiteSpace(issue.FilePath))
                    .Select(issue => issue.FilePath)
                    .ToList();
                break;

            default:
                return BadRequest(new { error = "scope must be all, selected or series" });
        }

        files = files.Distinct(StringComparer.Ordinal).ToList();
        if (files.Count == 0)
        {
            return Ok(new JobsController.BatchJobResponse(Guid.Empty.ToString(), 0));
        }

        var jobId = _previews.StartPreview(files);
        _logger.LogInformation(
            LoggingHelper.WithWebsitePrefix("Processing preview {JobId} started ({Scope}, {Count} file(s))"),
            jobId, LoggingHelper.SanitizeForLog(request.Scope), files.Count);
        return Ok(new JobsController.BatchJobResponse(jobId.ToString(), files.Count));
    }

    /// <summary>
    /// One page of a preview's results, sorted by series then path. <c>filter</c> is
    /// <c>changes</c> (the default), <c>all</c>, <c>renamed</c>, <c>tags</c>, <c>clashes</c> or
    /// <c>errors</c>; a <c>limit</c> outside 1 to 1000 becomes 200.
    /// </summary>
    [HttpGet("{jobId:guid}")]
    [FailureMessage("Error retrieving the preview")]
    public ActionResult<object> GetResults(
        Guid jobId,
        [FromQuery] string? filter = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = DefaultLimit)
    {
        if (!TryParseFilter(filter, out var parsedFilter))
        {
            return BadRequest(new { error = $"Unknown filter: {filter}" });
        }

        if (limit is < 1 or > MaxLimit)
        {
            limit = DefaultLimit;
        }

        var results = _previews.GetResults(jobId, parsedFilter, offset, limit);
        if (results is null)
        {
            return NotFound(new { error = "Preview not found. It may have been replaced by newer previews or lost in a restart." });
        }

        return Ok(new
        {
            jobId = results.JobId,
            status = results.Status,
            finished = results.Finished,
            totalFiles = results.TotalFiles,
            summary = new
            {
                @checked = results.Summary.Checked,
                changed = results.Summary.Changed,
                renamed = results.Summary.Renamed,
                tagsChanged = results.Summary.TagsChanged,
                clashes = results.Summary.Clashes,
                unchanged = results.Summary.Unchanged,
                errors = results.Summary.Errors
            },
            filter = FilterName(results.Filter),
            offset = results.Offset,
            limit = results.Limit,
            total = results.Total,
            items = results.Items.Select(ToResponse).ToList()
        });
    }

    private LibraryRoots Roots()
    {
        var settings = _settings.CurrentValue;
        return new LibraryRoots(settings.WatchedDirectory, settings.DuplicateDirectory);
    }

    /// <summary>The filters by name; a number, which an enum parse would take, is not one.</summary>
    private static readonly IReadOnlyDictionary<string, ProcessingPreviewFilter> Filters =
        new Dictionary<string, ProcessingPreviewFilter>(StringComparer.OrdinalIgnoreCase)
        {
            ["changes"] = ProcessingPreviewFilter.Changes,
            ["all"] = ProcessingPreviewFilter.All,
            ["renamed"] = ProcessingPreviewFilter.Renamed,
            ["tags"] = ProcessingPreviewFilter.Tags,
            ["clashes"] = ProcessingPreviewFilter.Clashes,
            ["errors"] = ProcessingPreviewFilter.Errors
        };

    private static bool TryParseFilter(string? filter, out ProcessingPreviewFilter parsed)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            parsed = ProcessingPreviewFilter.Changes;
            return true;
        }

        return Filters.TryGetValue(filter.Trim(), out parsed);
    }

    private static string FilterName(ProcessingPreviewFilter filter) =>
        Filters.First(entry => entry.Value == filter).Key;

    /// <summary>A file's preview as the routes send it.</summary>
    private static object ToResponse(ProcessingPreviewItem item) => new
    {
        filePath = item.FilePath,
        fileName = Path.GetFileName(item.FilePath),
        series = item.Series,
        newFilePath = item.NewFilePath,
        newFileName = item.NewFilePath is null ? null : Path.GetFileName(item.NewFilePath),
        renamed = item.Renames,
        tagChanges = item.TagChanges.Select(change => new { field = change.Field, before = change.Before, after = change.After }).ToList(),
        clash = item.Clash switch
        {
            ProcessingPreviewClash.ExistingFile => "existing",
            ProcessingPreviewClash.SameTargetInPreview => "preview",
            _ => null
        },
        clashWith = item.ClashWith,
        warning = item.Warning,
        error = item.Error,
        changed = item.HasChanges
    };

    /// <summary>What to preview.</summary>
    public class PreviewRequest
    {
        /// <summary><c>all</c>, <c>selected</c> or <c>series</c>.</summary>
        public string? Scope { get; set; }

        /// <summary>The files to preview, for the selected scope.</summary>
        public List<string>? Files { get; set; }

        /// <summary>The series to preview (its id in the series routes), for the series scope.</summary>
        public string? SeriesId { get; set; }
    }
}
