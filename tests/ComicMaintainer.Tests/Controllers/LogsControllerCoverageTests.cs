using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// Log types and filename restrictions of <see cref="LogsController"/> beyond
/// <see cref="LogsControllerTests"/>: every log type selects only its own
/// files, and a requested file name must belong to the requested type.
/// </summary>
public sealed class LogsControllerCoverageTests : IDisposable
{
    private readonly string _logDir = Path.Join(Path.GetTempPath(), "cm_logs_cov_" + Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings;
    private readonly LogsController _controller;

    public LogsControllerCoverageTests()
    {
        Directory.CreateDirectory(_logDir);
        _settings = new AppSettings { ConfigDirectory = _logDir };
        _controller = new LogsController(
            Mock.Of<IOptionsMonitor<AppSettings>>(m => m.CurrentValue == _settings),
            Mock.Of<ILogger<LogsController>>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_logDir, recursive: true); } catch (IOException) { /* best-effort cleanup */ } catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("auth")]
    [InlineData("AUTH")]
    public void GetLogFiles_ListsOnlyTheFilesOfTheRequestedType(string type)
    {
        WriteAllLogTypes();
        var prefix = type.ToLowerInvariant();

        var json = OkJson(_controller.GetLogFiles(type));

        var names = json.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("filename").GetString()).OrderBy(n => n).ToList();
        Assert.Equal(new[] { $"{prefix}20260101.log", $"{prefix}20260102.log" }, names);
        Assert.Equal(2, json.GetProperty("count").GetInt32());
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("auth")]
    public void GetLogs_ShowsTheNewestFileOfTheRequestedType(string type)
    {
        WriteAllLogTypes();

        var json = OkJson(_controller.GetLogs(type: type));

        Assert.Equal($"{type}20260102.log", json.GetProperty("filename").GetString());
        Assert.Equal($"{type} newest", json.GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("app", "app20260101.log")]
    [InlineData("watcher", "watcher20260101.log")]
    [InlineData("metadata", "metadata20260101.log")]
    [InlineData("auth", "auth20260101.log")]
    [InlineData("debug", "debug20260101.log")]
    [InlineData("anything-else", "debug20260101.log")]
    public void GetLogs_FilenameOfTheRequestedTypeIsServed(string type, string filename)
    {
        WriteAllLogTypes();

        var json = OkJson(_controller.GetLogs(type: type, filename: filename));

        Assert.Equal(filename, json.GetProperty("filename").GetString());
        Assert.Equal(File.ReadAllText(Path.Join(_logDir, filename)).TrimEnd(), json.GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("app", "watcher20260101.log")]
    [InlineData("app", "app20260101.txt")]
    [InlineData("watcher", "app20260101.log")]
    [InlineData("watcher", "watcher20260101.txt")]
    [InlineData("metadata", "auth20260101.log")]
    [InlineData("metadata", "metadata20260101.txt")]
    [InlineData("auth", "metadata20260101.log")]
    [InlineData("auth", "auth20260101.txt")]
    [InlineData("anything-else", "app20260101.log")]
    [InlineData("anything-else", "debug20260101.txt")]
    public void GetLogs_FilenameOfAnotherTypeIsRefusedEvenWhenItExists(string type, string filename)
    {
        WriteAllLogTypes();
        File.WriteAllText(Path.Join(_logDir, filename), "must not be served");

        var json = OkJson(_controller.GetLogs(type: type, filename: filename));

        Assert.Equal($"File does not match expected log type: {filename}", json.GetProperty("content").GetString());
        Assert.Equal(0, json.GetProperty("total_lines").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("filename").ValueKind);
    }

    [Theory]
    [InlineData("app")]
    [InlineData("watcher")]
    [InlineData("metadata")]
    [InlineData("auth")]
    public void GetLogs_MatchingFilenameThatDoesNotExistIsReportedAsNotFound(string type)
    {
        var json = OkJson(_controller.GetLogs(type: type, filename: $"{type}19990101.log"));

        Assert.Equal($"Log file not found: {type}19990101.log", json.GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("filename").ValueKind);
    }

    [Fact]
    public void GetLogFiles_WithoutConfigDirectoryReadsTheDefaultConfigFolder()
    {
        _settings.ConfigDirectory = null!;

        var result = _controller.GetLogFiles();

        if (Directory.Exists("/Config"))
        {
            var json = OkJson(result);
            Assert.Equal(Directory.GetFiles("/Config", "debug*.log").Length, json.GetProperty("count").GetInt32());
        }
        else
        {
            var error = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(500, error.StatusCode);
            Assert.Contains("/Config", JsonSerializer.SerializeToElement(error.Value).GetProperty("error").GetString());
        }
    }

    private void WriteAllLogTypes()
    {
        var older = DateTime.UtcNow.AddHours(-2);
        var newer = DateTime.UtcNow.AddHours(-1);
        foreach (var prefix in new[] { "app", "watcher", "metadata", "auth", "debug" })
        {
            var oldFile = Path.Join(_logDir, $"{prefix}20260101.log");
            var newFile = Path.Join(_logDir, $"{prefix}20260102.log");
            File.WriteAllText(oldFile, $"{prefix} oldest\n");
            File.WriteAllText(newFile, $"{prefix} newest\n");
            File.SetLastWriteTimeUtc(oldFile, older);
            File.SetLastWriteTimeUtc(newFile, newer);
        }
    }

    private static JsonElement OkJson(ActionResult<object> result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result.Result).Value);
}
