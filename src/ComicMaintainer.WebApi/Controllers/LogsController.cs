using ComicMaintainer.Core.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LogsController : ControllerBase
{
    private readonly AppSettings _settings;
    private readonly ILogger<LogsController> _logger;

    public LogsController(
        IOptions<AppSettings> settings,
        ILogger<LogsController> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    [HttpGet]
    public ActionResult<object> GetLogs([FromQuery] int lines = 500, [FromQuery] string type = "basic")
    {
        try
        {
            var configDir = _settings.ConfigDirectory ?? "/Config";
            
            // Find the most recent log file (Serilog uses rolling date suffix)
            var logFilePattern = "debug*.log";
            var logFiles = Directory.GetFiles(configDir, logFilePattern)
                .OrderByDescending(f => System.IO.File.GetLastWriteTime(f))
                .ToArray();

            if (logFiles.Length == 0)
            {
                return Ok(new
                {
                    content = $"No log files found matching pattern: {logFilePattern}",
                    total_lines = 0,
                    shown_lines = 0
                });
            }

            var logFilePath = logFiles[0]; // Most recent log file

            // Read the file
            var allLines = System.IO.File.ReadAllLines(logFilePath);
            var totalLines = allLines.Length;

            // Get the last N lines (or all if lines is 0)
            var linesToShow = lines == 0 ? allLines : allLines.TakeLast(lines).ToArray();
            var content = string.Join(Environment.NewLine, linesToShow);

            return Ok(new
            {
                content,
                total_lines = totalLines,
                shown_lines = linesToShow.Length
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading logs");
            return StatusCode(500, new
            {
                content = $"Error reading log file: {ex.Message}",
                total_lines = 0,
                shown_lines = 0
            });
        }
    }
}
