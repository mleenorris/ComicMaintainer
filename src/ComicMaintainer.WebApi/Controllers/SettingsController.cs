using ComicMaintainer.Core.Configuration;
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

    [HttpGet("filename-format")]
    public ActionResult<object> GetFilenameFormat()
    {
        return Ok(new { format = _appSettings.Value.FilenameFormat });
    }

    [HttpPost("filename-format")]
    public ActionResult SetFilenameFormat([FromBody] FilenameFormatRequest request)
    {
        _logger.LogInformation("Filename format update requested: {Format}", request.Format);
        // TODO: Implement persistence - settings are currently read-only from configuration
        // In a full implementation, this would update a user preferences table in the database
        _logger.LogWarning("Filename format changes are not persisted - requires database implementation");
        return Ok(new { message = "Setting received but not persisted (read-only)" });
    }

    [HttpGet("issue-number-padding")]
    public ActionResult<object> GetIssueNumberPadding()
    {
        return Ok(new { padding = _appSettings.Value.IssueNumberPadding });
    }

    [HttpPost("issue-number-padding")]
    public ActionResult SetIssueNumberPadding([FromBody] IssueNumberPaddingRequest request)
    {
        _logger.LogInformation("Issue number padding update requested: {Padding}", request.Padding);
        return Ok();
    }

    [HttpGet("watcher-enabled")]
    public ActionResult<object> GetWatcherEnabled()
    {
        return Ok(new { enabled = _appSettings.Value.WatcherEnabled });
    }

    [HttpPost("watcher-enabled")]
    public ActionResult SetWatcherEnabled([FromBody] WatcherEnabledRequest request)
    {
        _logger.LogInformation("Watcher enabled update requested: {Enabled}", request.Enabled);
        return Ok();
    }

    [HttpGet("log-max-bytes")]
    public ActionResult<object> GetLogMaxBytes()
    {
        return Ok(new { maxBytes = _appSettings.Value.LogMaxBytes });
    }

    [HttpPost("log-max-bytes")]
    public ActionResult SetLogMaxBytes([FromBody] LogMaxBytesRequest request)
    {
        _logger.LogInformation("Log max bytes update requested: {MaxBytes}", request.MaxBytes);
        return Ok();
    }

    [HttpGet("github-token")]
    public ActionResult<object> GetGitHubToken()
    {
        // Don't return the actual token for security
        return Ok(new { hasToken = !string.IsNullOrEmpty(_appSettings.Value.GitHubToken) });
    }

    [HttpPost("github-token")]
    public ActionResult SetGitHubToken([FromBody] GitHubTokenRequest request)
    {
        _logger.LogInformation("GitHub token update requested");
        return Ok();
    }

    [HttpGet("github-repository")]
    public ActionResult<object> GetGitHubRepository()
    {
        return Ok(new { repository = _appSettings.Value.GitHubRepository ?? "" });
    }

    [HttpPost("github-repository")]
    public ActionResult SetGitHubRepository([FromBody] GitHubRepositoryRequest request)
    {
        _logger.LogInformation("GitHub repository update requested: {Repository}", request.Repository);
        return Ok();
    }

    [HttpGet("github-issue-assignee")]
    public ActionResult<object> GetGitHubIssueAssignee()
    {
        return Ok(new { assignee = _appSettings.Value.GitHubIssueAssignee ?? "" });
    }

    [HttpPost("github-issue-assignee")]
    public ActionResult SetGitHubIssueAssignee([FromBody] GitHubIssueAssigneeRequest request)
    {
        _logger.LogInformation("GitHub issue assignee update requested: {Assignee}", request.Assignee);
        return Ok();
    }

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
}
