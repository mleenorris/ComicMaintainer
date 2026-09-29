using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using ComicMaintainer.WebApi.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// The diagnostics snapshot is what an operator reads when something has
/// already gone wrong, so the two properties that matter are that it reports
/// the truth and that it still answers when individual sections fail.
/// </summary>
public class DiagnosticsControllerTests : IDisposable
{
    private readonly string _configDir;
    private readonly string _watchedDir;
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IFileWatcherService> _watcher = new();
    private readonly Mock<IJobStateStore> _jobStateStore = new();

    public DiagnosticsControllerTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "cm_diag_cfg_" + Guid.NewGuid().ToString("N"));
        _watchedDir = Path.Combine(Path.GetTempPath(), "cm_diag_watch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
        Directory.CreateDirectory(_watchedDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_configDir, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_watchedDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private DiagnosticsController CreateController()
    {
        var settings = new AppSettings
        {
            ConfigDirectory = _configDir,
            WatchedDirectory = _watchedDir,
            DuplicateDirectory = Path.Combine(_watchedDir, "does-not-exist"),
            WatcherEnableRename = true,
            WatcherEnableNormalize = false
        };

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(Environments.Production);

        var services = new ServiceCollection();
        services.AddSingleton(environment.Object);
        var provider = services.BuildServiceProvider();

        var healthChecks = new ServiceCollection()
            .AddLogging()
            .AddHealthChecks()
            .Services
            .BuildServiceProvider()
            .GetRequiredService<HealthCheckService>();

        var controller = new DiagnosticsController(
            Mock.Of<IOptionsMonitor<AppSettings>>(m => m.CurrentValue == settings),
            _fileStore.Object,
            _watcher.Object,
            _jobStateStore.Object,
            healthChecks,
            NullLogger<DiagnosticsController>.Instance);

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        httpContext.Items[RequestCorrelationMiddleware.ItemsKey] = "diag-ref";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static JsonElement Serialize(ActionResult<object> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return JsonSerializer.SerializeToElement(ok.Value);
    }

    [Fact]
    public async Task ReportsWatcherLibraryAndStorageState()
    {
        _watcher.SetupGet(w => w.IsRunning).Returns(true);
        _fileStore
            .Setup(f => f.GetFileCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((12, 7, 5, 2));
        _jobStateStore
            .Setup(j => j.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProcessingJob>
            {
                new() { JobId = Guid.NewGuid(), Status = JobStatus.Running, StartTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) },
                new() { JobId = Guid.NewGuid(), Status = JobStatus.Interrupted, StartTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) }
            });

        var json = Serialize(await CreateController().GetDiagnostics());

        Assert.Equal("diag-ref", json.GetProperty("correlationId").GetString());
        Assert.True(json.GetProperty("watcher").GetProperty("running").GetBoolean());
        Assert.True(json.GetProperty("watcher").GetProperty("renameEnabled").GetBoolean());
        Assert.False(json.GetProperty("watcher").GetProperty("normalizeEnabled").GetBoolean());

        Assert.Equal(12, json.GetProperty("library").GetProperty("total").GetInt32());
        Assert.Equal(2, json.GetProperty("library").GetProperty("duplicates").GetInt32());

        Assert.Equal(1, json.GetProperty("jobs").GetProperty("running").GetInt32());
        Assert.Equal(1, json.GetProperty("jobs").GetProperty("interrupted").GetInt32());

        // A missing or read-only mount is the usual reason a library stops
        // updating, so existence/writability must be reported per directory.
        var storage = json.GetProperty("storage").EnumerateArray().ToList();
        var watched = storage.Single(e => e.GetProperty("name").GetString() == "Watched");
        Assert.True(watched.GetProperty("exists").GetBoolean());
        Assert.True(watched.GetProperty("writable").GetBoolean());

        var duplicates = storage.Single(e => e.GetProperty("name").GetString() == "Duplicates");
        Assert.False(duplicates.GetProperty("exists").GetBoolean());
        Assert.False(duplicates.GetProperty("writable").GetBoolean());

        Assert.True(json.GetProperty("application").GetProperty("uptimeSeconds").GetInt64() >= 0);
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("runtime").GetProperty("framework").GetString()));
    }

    [Fact]
    public async Task LeavesNoWriteProbeBehind()
    {
        _fileStore
            .Setup(f => f.GetFileCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((0, 0, 0, 0));
        _jobStateStore
            .Setup(j => j.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProcessingJob>());

        await CreateController().GetDiagnostics();

        Assert.Empty(Directory.GetFileSystemEntries(_watchedDir));
    }

    [Fact]
    public async Task ListsLogFilesWithTheirSize()
    {
        File.WriteAllText(Path.Combine(_configDir, "debug.log"), "hello");
        File.WriteAllText(Path.Combine(_configDir, "not-a-log.txt"), "ignored");
        _fileStore
            .Setup(f => f.GetFileCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((0, 0, 0, 0));
        _jobStateStore
            .Setup(j => j.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProcessingJob>());

        var json = Serialize(await CreateController().GetDiagnostics());

        var files = json.GetProperty("logs").GetProperty("files").EnumerateArray().ToList();
        var debugLog = Assert.Single(files);
        Assert.Equal("debug.log", debugLog.GetProperty("name").GetString());
        Assert.Equal(5, debugLog.GetProperty("sizeBytes").GetInt64());
    }

    [Fact]
    public async Task StillAnswersWhenASectionCannotBeRead()
    {
        // A diagnostics page that itself 500s is worse than useless: each
        // section must degrade on its own.
        _fileStore
            .Setup(f => f.GetFileCountsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        _jobStateStore
            .Setup(j => j.GetAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        _watcher.SetupGet(w => w.IsRunning).Throws(new InvalidOperationException("watcher blew up"));

        var json = Serialize(await CreateController().GetDiagnostics());

        Assert.True(json.GetProperty("library").TryGetProperty("error", out _));
        Assert.True(json.GetProperty("jobs").TryGetProperty("error", out _));
        Assert.True(json.GetProperty("watcher").TryGetProperty("error", out _));
        // Sections that did work are still reported.
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("application").GetProperty("version").GetString()));
        // The underlying exception text is never echoed back to the client.
        Assert.DoesNotContain("database is locked", json.ToString());
    }
}
