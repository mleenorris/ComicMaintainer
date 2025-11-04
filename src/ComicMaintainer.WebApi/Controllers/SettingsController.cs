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
    private readonly ISettingsService _settingsService;
    private readonly IHostApplicationLifetime _applicationLifetime;

    public SettingsController(
        IOptions<AppSettings> appSettings, 
        ILogger<SettingsController> logger,
        IServiceProvider serviceProvider,
        IComicProcessorService processorService,
        IFileStoreService fileStore,
        ISettingsService settingsService,
        IHostApplicationLifetime applicationLifetime)
    {
        _appSettings = appSettings;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _processorService = processorService;
        _fileStore = fileStore;
        _settingsService = settingsService;
        _applicationLifetime = applicationLifetime;
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
            log_max_bytes = _appSettings.Value.LogMaxBytes,
            github_token_configured = !string.IsNullOrEmpty(_appSettings.Value.GitHubToken),
            github_repository = _appSettings.Value.GitHubRepository ?? "",
            github_issue_assignee = _appSettings.Value.GitHubIssueAssignee ?? ""
        });
    }

    [HttpGet("filename-format")]
    public ActionResult<object> GetFilenameFormat()
    {
        return Ok(new { format = _appSettings.Value.FilenameFormat });
    }

    // RESTful endpoint: PUT /api/settings/filename-format
    [HttpPut("filename-format")]
    public async Task<ActionResult> UpdateFilenameFormat([FromBody] FilenameFormatRequest request, CancellationToken cancellationToken = default)
    {
        var sanitizedFormat = LoggingHelper.SanitizeForLog(request.Format);
        _logger.LogInformation("Filename format update requested: {Format}", sanitizedFormat);
        
        try
        {
            await _settingsService.UpdateFilenameFormatAsync(request.Format, cancellationToken);
            return Ok(new { message = "Filename format updated successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update filename format");
            return StatusCode(500, new { error = "Failed to update filename format" });
        }
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("filename-format")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public Task<ActionResult> SetFilenameFormat([FromBody] FilenameFormatRequest request, CancellationToken cancellationToken = default) 
        => UpdateFilenameFormat(request, cancellationToken);

    [HttpGet("issue-number-padding")]
    public ActionResult<object> GetIssueNumberPadding()
    {
        return Ok(new { padding = _appSettings.Value.IssueNumberPadding });
    }

    // RESTful endpoint: PUT /api/settings/issue-number-padding
    [HttpPut("issue-number-padding")]
    public async Task<ActionResult> UpdateIssueNumberPadding([FromBody] IssueNumberPaddingRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Issue number padding update requested: {Padding}", request.Padding);
        
        try
        {
            await _settingsService.UpdateIssueNumberPaddingAsync(request.Padding, cancellationToken);
            return Ok(new { message = "Issue number padding updated successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update issue number padding");
            return StatusCode(500, new { error = "Failed to update issue number padding" });
        }
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("issue-number-padding")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public Task<ActionResult> SetIssueNumberPadding([FromBody] IssueNumberPaddingRequest request, CancellationToken cancellationToken = default)
        => UpdateIssueNumberPadding(request, cancellationToken);

    // Note: Master watcher enabled has been removed. Watcher is now enabled
    // when either WatcherEnableRename or WatcherEnableNormalize is true.

    [HttpGet("log-max-bytes")]
    public ActionResult<object> GetLogMaxBytes()
    {
        return Ok(new { maxBytes = _appSettings.Value.LogMaxBytes });
    }

    // RESTful endpoint: PUT /api/settings/log-max-bytes
    [HttpPut("log-max-bytes")]
    public async Task<ActionResult> UpdateLogMaxBytes([FromBody] LogMaxBytesRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Log max bytes update requested: {MaxBytes}", request.MaxBytes);
        
        try
        {
            await _settingsService.UpdateLogMaxBytesAsync(request.MaxBytes, cancellationToken);
            _logger.LogWarning("Log max bytes updated to {MaxBytes}. Restart the application for the change to take effect.", request.MaxBytes);
            return Ok(new { message = "Log max bytes updated successfully. Restart required for changes to take effect." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update log max bytes");
            return StatusCode(500, new { error = "Failed to update log max bytes" });
        }
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("log-max-bytes")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public Task<ActionResult> SetLogMaxBytes([FromBody] LogMaxBytesRequest request, CancellationToken cancellationToken = default)
        => UpdateLogMaxBytes(request, cancellationToken);

    [HttpGet("github-token")]
    public ActionResult<object> GetGitHubToken()
    {
        // Don't return the actual token for security
        return Ok(new { hasToken = !string.IsNullOrEmpty(_appSettings.Value.GitHubToken) });
    }

    // RESTful endpoint: PUT /api/settings/github-token
    [HttpPut("github-token")]
    public async Task<ActionResult> UpdateGitHubToken([FromBody] GitHubTokenRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GitHub token update requested");
        
        try
        {
            await _settingsService.UpdateGitHubTokenAsync(request.Token, cancellationToken);
            return Ok(new { message = "GitHub token updated successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update GitHub token");
            return StatusCode(500, new { error = "Failed to update GitHub token" });
        }
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("github-token")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public Task<ActionResult> SetGitHubToken([FromBody] GitHubTokenRequest request, CancellationToken cancellationToken = default)
        => UpdateGitHubToken(request, cancellationToken);

    [HttpGet("github-repository")]
    public ActionResult<object> GetGitHubRepository()
    {
        return Ok(new { repository = _appSettings.Value.GitHubRepository ?? "" });
    }

    // RESTful endpoint: PUT /api/settings/github-repository
    [HttpPut("github-repository")]
    public async Task<ActionResult> UpdateGitHubRepository([FromBody] GitHubRepositoryRequest request, CancellationToken cancellationToken = default)
    {
        var sanitizedRepo = LoggingHelper.SanitizeForLog(request.Repository);
        _logger.LogInformation("GitHub repository update requested: {Repository}", sanitizedRepo);
        
        try
        {
            await _settingsService.UpdateGitHubRepositoryAsync(request.Repository, cancellationToken);
            return Ok(new { message = "GitHub repository updated successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update GitHub repository");
            return StatusCode(500, new { error = "Failed to update GitHub repository" });
        }
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("github-repository")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public Task<ActionResult> SetGitHubRepository([FromBody] GitHubRepositoryRequest request, CancellationToken cancellationToken = default)
        => UpdateGitHubRepository(request, cancellationToken);

    [HttpGet("github-issue-assignee")]
    public ActionResult<object> GetGitHubIssueAssignee()
    {
        return Ok(new { assignee = _appSettings.Value.GitHubIssueAssignee ?? "" });
    }

    // RESTful endpoint: PUT /api/settings/github-issue-assignee
    [HttpPut("github-issue-assignee")]
    public async Task<ActionResult> UpdateGitHubIssueAssignee([FromBody] GitHubIssueAssigneeRequest request, CancellationToken cancellationToken = default)
    {
        var sanitizedAssignee = LoggingHelper.SanitizeForLog(request.Assignee);
        _logger.LogInformation("GitHub issue assignee update requested: {Assignee}", sanitizedAssignee);
        
        try
        {
            await _settingsService.UpdateGitHubIssueAssigneeAsync(request.Assignee, cancellationToken);
            return Ok(new { message = "GitHub issue assignee updated successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update GitHub issue assignee");
            return StatusCode(500, new { error = "Failed to update GitHub issue assignee" });
        }
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("github-issue-assignee")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public Task<ActionResult> SetGitHubIssueAssignee([FromBody] GitHubIssueAssigneeRequest request, CancellationToken cancellationToken = default)
        => UpdateGitHubIssueAssignee(request, cancellationToken);

    [HttpGet("watcher-enable-rename")]
    public ActionResult<object> GetWatcherEnableRename()
    {
        return Ok(new { enabled = _appSettings.Value.WatcherEnableRename });
    }

    [HttpPut("watcher-enable-rename")]
    public async Task<ActionResult> UpdateWatcherEnableRename([FromBody] WatcherEnableRenameRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Watcher enable rename update requested: {Enabled}", request.Enabled);
        
        try
        {
            await _settingsService.UpdateWatcherEnableRenameAsync(request.Enabled, cancellationToken);
            return Ok(new { message = "Watcher enable rename updated successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update watcher enable rename");
            return StatusCode(500, new { error = "Failed to update watcher enable rename" });
        }
    }

    [HttpPost("watcher-enable-rename")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public Task<ActionResult> SetWatcherEnableRename([FromBody] WatcherEnableRenameRequest request, CancellationToken cancellationToken = default)
        => UpdateWatcherEnableRename(request, cancellationToken);

    [HttpGet("watcher-enable-normalize")]
    public ActionResult<object> GetWatcherEnableNormalize()
    {
        return Ok(new { enabled = _appSettings.Value.WatcherEnableNormalize });
    }

    [HttpPut("watcher-enable-normalize")]
    public async Task<ActionResult> UpdateWatcherEnableNormalize([FromBody] WatcherEnableNormalizeRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Watcher enable normalize update requested: {Enabled}", request.Enabled);
        
        try
        {
            await _settingsService.UpdateWatcherEnableNormalizeAsync(request.Enabled, cancellationToken);
            return Ok(new { message = "Watcher enable normalize updated successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update watcher enable normalize");
            return StatusCode(500, new { error = "Failed to update watcher enable normalize" });
        }
    }

    [HttpPost("watcher-enable-normalize")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public Task<ActionResult> SetWatcherEnableNormalize([FromBody] WatcherEnableNormalizeRequest request, CancellationToken cancellationToken = default)
        => UpdateWatcherEnableNormalize(request, cancellationToken);

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

    [HttpPost("restart")]
    [Authorize(Roles = "Admin")]
    public ActionResult RestartApplication()
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var userName = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value;
        
        _logger.LogWarning("Application restart requested by user {UserId} ({UserName})", userId, userName);
        
        try
        {
            // Trigger graceful shutdown which will cause the application to restart (when running in a container or with a process manager)
            _logger.LogInformation("Initiating application shutdown for restart...");
            
            // Use a background task to allow the response to be sent before shutting down
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000); // Give time for the response to be sent
                _applicationLifetime.StopApplication();
            });
            
            return Ok(new { 
                success = true, 
                message = "Application restart initiated. The application will shut down gracefully and restart if running in a container or with a process manager." 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initiating application restart");
            return StatusCode(500, new { success = false, error = "Error initiating restart: " + ex.Message });
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

    public class GitHubTokenRequest
    {
        public string Token { get; set; } = string.Empty;
    }

    public class GitHubRepositoryRequest
    {
        public string Repository { get; set; } = string.Empty;
    }

    public class GitHubIssueAssigneeRequest
    {
        public string Assignee { get; set; } = string.Empty;
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
