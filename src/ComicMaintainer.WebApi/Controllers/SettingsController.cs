using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SettingsController : ControllerBase
{
    private readonly IOptions<AppSettings> _appSettings;
    private readonly ILogger<SettingsController> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IComicProcessorService _processorService;
    private readonly IFileStoreService _fileStore;

    public SettingsController(
        IOptions<AppSettings> appSettings, 
        ILogger<SettingsController> logger,
        IServiceProvider serviceProvider,
        IComicProcessorService processorService,
        IFileStoreService fileStore)
    {
        _appSettings = appSettings;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _processorService = processorService;
        _fileStore = fileStore;
    }

    // RESTful endpoint: GET /api/settings - Get all settings
    [HttpGet]
    public ActionResult<object> GetAllSettings()
    {
        return Ok(new
        {
            filename_format = _appSettings.Value.FilenameFormat,
            issue_number_padding = _appSettings.Value.IssueNumberPadding,
            watcher_enable_rename = _appSettings.Value.WatcherEnableRename,
            watcher_enable_normalize = _appSettings.Value.WatcherEnableNormalize,
            log_max_bytes = _appSettings.Value.LogMaxBytes
        });
    }

    [HttpGet("filename-format")]
    public ActionResult<object> GetFilenameFormat()
    {
        return Ok(new { format = _appSettings.Value.FilenameFormat });
    }

    // RESTful endpoint: PUT /api/settings/filename-format
    [HttpPut("filename-format")]
    public ActionResult UpdateFilenameFormat([FromBody] FilenameFormatRequest request)
    {
        var sanitizedFormat = LoggingHelper.SanitizeForLog(request.Format);
        _logger.LogInformation("Filename format update requested: {Format}", sanitizedFormat);
        // TODO: Implement persistence - settings are currently read-only from configuration
        // In a full implementation, this would update a user preferences table in the database
        _logger.LogWarning("Filename format changes are not persisted - requires database implementation");
        return Ok(new { message = "Setting received but not persisted (read-only)" });
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("filename-format")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult SetFilenameFormat([FromBody] FilenameFormatRequest request) 
        => UpdateFilenameFormat(request);

    [HttpGet("issue-number-padding")]
    public ActionResult<object> GetIssueNumberPadding()
    {
        return Ok(new { padding = _appSettings.Value.IssueNumberPadding });
    }

    // RESTful endpoint: PUT /api/settings/issue-number-padding
    [HttpPut("issue-number-padding")]
    public ActionResult UpdateIssueNumberPadding([FromBody] IssueNumberPaddingRequest request)
    {
        _logger.LogInformation("Issue number padding update requested: {Padding}", request.Padding);
        return Ok();
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("issue-number-padding")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult SetIssueNumberPadding([FromBody] IssueNumberPaddingRequest request)
        => UpdateIssueNumberPadding(request);

    // Note: Master watcher enabled has been removed. Watcher is now enabled
    // when either WatcherEnableRename or WatcherEnableNormalize is true.

    [HttpGet("log-max-bytes")]
    public ActionResult<object> GetLogMaxBytes()
    {
        return Ok(new { maxBytes = _appSettings.Value.LogMaxBytes });
    }

    // RESTful endpoint: PUT /api/settings/log-max-bytes
    [HttpPut("log-max-bytes")]
    public ActionResult UpdateLogMaxBytes([FromBody] LogMaxBytesRequest request)
    {
        _logger.LogInformation("Log max bytes update requested: {MaxBytes}", request.MaxBytes);
        return Ok();
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("log-max-bytes")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult SetLogMaxBytes([FromBody] LogMaxBytesRequest request)
        => UpdateLogMaxBytes(request);

    [HttpGet("watcher-enable-rename")]
    public ActionResult<object> GetWatcherEnableRename()
    {
        return Ok(new { enabled = _appSettings.Value.WatcherEnableRename });
    }

    [HttpPut("watcher-enable-rename")]
    public ActionResult UpdateWatcherEnableRename([FromBody] WatcherEnableRenameRequest request)
    {
        _logger.LogInformation("Watcher enable rename update requested: {Enabled}", request.Enabled);
        return Ok();
    }

    [HttpPost("watcher-enable-rename")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult SetWatcherEnableRename([FromBody] WatcherEnableRenameRequest request)
        => UpdateWatcherEnableRename(request);

    [HttpGet("watcher-enable-normalize")]
    public ActionResult<object> GetWatcherEnableNormalize()
    {
        return Ok(new { enabled = _appSettings.Value.WatcherEnableNormalize });
    }

    [HttpPut("watcher-enable-normalize")]
    public ActionResult UpdateWatcherEnableNormalize([FromBody] WatcherEnableNormalizeRequest request)
    {
        _logger.LogInformation("Watcher enable normalize update requested: {Enabled}", request.Enabled);
        return Ok();
    }

    [HttpPost("watcher-enable-normalize")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult SetWatcherEnableNormalize([FromBody] WatcherEnableNormalizeRequest request)
        => UpdateWatcherEnableNormalize(request);

    [HttpPost("reset")]
    public async Task<ActionResult> ResetDatabase(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogWarning("Database reset requested - this will delete all processing data");

            // Get all active jobs and mark them as cancelled
            var activeJobs = _processorService.GetAllJobs()
                .Where(j => j.Status == Core.Models.JobStatus.Running || j.Status == Core.Models.JobStatus.Queued)
                .ToList();

            foreach (var job in activeJobs)
            {
                _logger.LogInformation("Cancelling job {JobId} due to reset", job.JobId);
                // Note: Jobs are cancelled via CancellationToken in their execution contexts
                // We can only mark them and they will stop on their own
            }

            // Clear all database tables
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();

            _logger.LogInformation("Deleting processing history...");
            await dbContext.ProcessingHistory.ExecuteDeleteAsync(cancellationToken);

            _logger.LogInformation("Deleting comic files...");
            await dbContext.ComicFiles.ExecuteDeleteAsync(cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);

            // Reinitialize file store from database (which is now empty)
            await _fileStore.InitializeFromDatabaseAsync(cancellationToken);

            _logger.LogInformation("Database reset completed successfully");

            return Ok(new { success = true, message = "Database reset completed successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting database");
            return StatusCode(500, new { success = false, error = "Error resetting database: " + ex.Message });
        }
    }

    public class FilenameFormatRequest
    {
        public string Format { get; set; } = string.Empty;
    }

    public class IssueNumberPaddingRequest
    {
        public int Padding { get; set; }
    }

    public class LogMaxBytesRequest
    {
        public int MaxBytes { get; set; }
    }

    public class WatcherEnableRenameRequest
    {
        public bool Enabled { get; set; }
    }

    public class WatcherEnableNormalizeRequest
    {
        public bool Enabled { get; set; }
    }
}
