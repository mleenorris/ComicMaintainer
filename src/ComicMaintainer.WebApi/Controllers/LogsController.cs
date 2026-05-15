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
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<LogsController> _logger;

    public LogsController(
        IOptionsMonitor<AppSettings> settings,
        ILogger<LogsController> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    [HttpGet("files")]
    public ActionResult<object> GetLogFiles([FromQuery] string type = "debug")
    {
        try
        {
            var configDir = _settings.CurrentValue.ConfigDirectory ?? "/Config";
            
            // Determine log file pattern based on type
            string logFilePattern = type.ToLower() switch
            {
                "app" => "app*.log",
                "watcher" => "watcher*.log",
                "debug" => "debug*.log",
                _ => "debug*.log"
            };
            
            // Find all log files matching the pattern
            var logFiles = Directory.GetFiles(configDir, logFilePattern)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => new
                {
                    filename = f.Name,
                    last_modified = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    size_mb = Math.Round(f.Length / 1024.0 / 1024.0, 2)
                })
                .ToArray();

            return Ok(new
            {
                files = logFiles,
                count = logFiles.Length
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing log files");
            return StatusCode(500, new
            {
                error = $"Error listing log files: {ex.Message}",
                files = Array.Empty<object>(),
                count = 0
            });
        }
    }

    [HttpGet]
    public ActionResult<object> GetLogs([FromQuery] int lines = 500, [FromQuery] string type = "debug", [FromQuery] string? filename = null)
    {
        try
        {
            var configDir = _settings.CurrentValue.ConfigDirectory ?? "/Config";
            
            // Determine log file pattern based on type
            string logFilePattern = type.ToLower() switch
            {
                "app" => "app*.log",
                "watcher" => "watcher*.log",
                "debug" => "debug*.log",
                _ => "debug*.log"
            };
            
            string logFilePath;
            
            if (!string.IsNullOrEmpty(filename))
            {
                // Use specific filename if provided
                // Sanitize filename to prevent directory traversal
                var sanitizedFilename = Path.GetFileName(filename);
                logFilePath = Path.Combine(configDir, sanitizedFilename);
                
                // Verify the file exists and matches the pattern
                if (!System.IO.File.Exists(logFilePath))
                {
                    return Ok(new
                    {
                        content = $"Log file not found: {sanitizedFilename}",
                        total_lines = 0,
                        shown_lines = 0,
                        filename = (string?)null
                    });
                }
                
                // Verify filename matches the expected pattern for the type
                var filenameOnly = Path.GetFileName(logFilePath);
                var matchesPattern = type.ToLower() switch
                {
                    "app" => filenameOnly.StartsWith("app") && filenameOnly.EndsWith(".log"),
                    "watcher" => filenameOnly.StartsWith("watcher") && filenameOnly.EndsWith(".log"),
                    "debug" => filenameOnly.StartsWith("debug") && filenameOnly.EndsWith(".log"),
                    _ => filenameOnly.StartsWith("debug") && filenameOnly.EndsWith(".log")
                };
                
                if (!matchesPattern)
                {
                    return Ok(new
                    {
                        content = $"File does not match expected log type: {sanitizedFilename}",
                        total_lines = 0,
                        shown_lines = 0,
                        filename = (string?)null
                    });
                }
            }
            else
            {
                // Find the most recent log file (Serilog uses rolling date suffix)
                var logFiles = Directory.GetFiles(configDir, logFilePattern)
                    .OrderByDescending(f => System.IO.File.GetLastWriteTime(f))
                    .ToArray();

                if (logFiles.Length == 0)
                {
                    return Ok(new
                    {
                        content = $"No log files found matching pattern: {logFilePattern}",
                        total_lines = 0,
                        shown_lines = 0,
                        filename = (string?)null
                    });
                }

                logFilePath = logFiles[0]; // Most recent log file
            }

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
                shown_lines = linesToShow.Length,
                filename = Path.GetFileName(logFilePath)
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
