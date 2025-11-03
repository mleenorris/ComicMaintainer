using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Utilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly IOptions<AppSettings> _appSettings;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(IOptions<AppSettings> appSettings, ILogger<SettingsController> logger)
    {
        _appSettings = appSettings;
        _logger = logger;
    }

    // RESTful endpoint: GET /api/settings - Get all settings
    [HttpGet]
    public ActionResult<object> GetAllSettings()
    {
        return Ok(new
        {
            filename_format = _appSettings.Value.FilenameFormat,
            issue_number_padding = _appSettings.Value.IssueNumberPadding,
            watcher_enabled = _appSettings.Value.WatcherEnabled,
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

    [HttpGet("watcher-enabled")]
    public ActionResult<object> GetWatcherEnabled()
    {
        return Ok(new { enabled = _appSettings.Value.WatcherEnabled });
    }

    // RESTful endpoint: PUT /api/settings/watcher-enabled
    [HttpPut("watcher-enabled")]
    public ActionResult UpdateWatcherEnabled([FromBody] WatcherEnabledRequest request)
    {
        _logger.LogInformation("Watcher enabled update requested: {Enabled}", request.Enabled);
        return Ok();
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("watcher-enabled")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult SetWatcherEnabled([FromBody] WatcherEnabledRequest request)
        => UpdateWatcherEnabled(request);

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

    [HttpGet("github-token")]
    public ActionResult<object> GetGitHubToken()
    {
        // Don't return the actual token for security
        return Ok(new { hasToken = !string.IsNullOrEmpty(_appSettings.Value.GitHubToken) });
    }

    // RESTful endpoint: PUT /api/settings/github-token
    [HttpPut("github-token")]
    public ActionResult UpdateGitHubToken([FromBody] GitHubTokenRequest request)
    {
        _logger.LogInformation("GitHub token update requested");
        return Ok();
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("github-token")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult SetGitHubToken([FromBody] GitHubTokenRequest request)
        => UpdateGitHubToken(request);

    [HttpGet("github-repository")]
    public ActionResult<object> GetGitHubRepository()
    {
        return Ok(new { repository = _appSettings.Value.GitHubRepository ?? "" });
    }

    // RESTful endpoint: PUT /api/settings/github-repository
    [HttpPut("github-repository")]
    public ActionResult UpdateGitHubRepository([FromBody] GitHubRepositoryRequest request)
    {
        var sanitizedRepo = LoggingHelper.SanitizeForLog(request.Repository);
        _logger.LogInformation("GitHub repository update requested: {Repository}", sanitizedRepo);
        return Ok();
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("github-repository")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult SetGitHubRepository([FromBody] GitHubRepositoryRequest request)
        => UpdateGitHubRepository(request);

    [HttpGet("github-issue-assignee")]
    public ActionResult<object> GetGitHubIssueAssignee()
    {
        return Ok(new { assignee = _appSettings.Value.GitHubIssueAssignee ?? "" });
    }

    // RESTful endpoint: PUT /api/settings/github-issue-assignee
    [HttpPut("github-issue-assignee")]
    public ActionResult UpdateGitHubIssueAssignee([FromBody] GitHubIssueAssigneeRequest request)
    {
        var sanitizedAssignee = LoggingHelper.SanitizeForLog(request.Assignee);
        _logger.LogInformation("GitHub issue assignee update requested: {Assignee}", sanitizedAssignee);
        return Ok();
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("github-issue-assignee")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult SetGitHubIssueAssignee([FromBody] GitHubIssueAssigneeRequest request)
        => UpdateGitHubIssueAssignee(request);

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

    public class FilenameFormatRequest
    {
        public string Format { get; set; } = string.Empty;
    }

    public class IssueNumberPaddingRequest
    {
        public int Padding { get; set; }
    }

    public class WatcherEnabledRequest
    {
        public bool Enabled { get; set; }
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
