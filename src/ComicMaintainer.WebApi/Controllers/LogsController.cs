using ComicMaintainer.Core.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
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
    public ActionResult<object> GetLogs([FromQuery] int lines = 500)
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

            // Use streaming for memory efficiency with large log files
            string[] linesToShow;
            int totalLines;
            
            // Set a reasonable maximum to prevent memory issues
            const int MAX_LINES = 10000;
            int effectiveLines = lines == 0 ? MAX_LINES : Math.Min(lines, MAX_LINES);
            
            // Use ReadLines for streaming and take last N lines
            var allLinesEnumerable = System.IO.File.ReadLines(logFilePath);
            totalLines = 0;
            
            // Count lines efficiently while building queue of last N lines
            var queue = new Queue<string>(effectiveLines);
            foreach (var line in allLinesEnumerable)
            {
                totalLines++;
                if (queue.Count >= effectiveLines)
                    queue.Dequeue();
                queue.Enqueue(line);
            }
            
            linesToShow = queue.ToArray();

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
