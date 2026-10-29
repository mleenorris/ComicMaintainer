using System.Text;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// Degraded inputs of <see cref="DiagnosticsController"/> beyond
/// <see cref="DiagnosticsControllerTests"/>: failing health checks, unset or
/// missing directories, a directory that exists but cannot be written and a log
/// file that cannot be inspected. Each must be reported in its own section
/// without failing the snapshot.
/// </summary>
public sealed class DiagnosticsControllerCoverageTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "cm_diag_cov_" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IJobStateStore> _jobStateStore = new();
    private readonly Mock<ILogger<DiagnosticsController>> _logger = new();
    private readonly List<string> _overlongDirectories = new();

    public DiagnosticsControllerCoverageTests()
    {
        Directory.CreateDirectory(_root);
        _fileStore.Setup(f => f.GetFileCountsAsync(It.IsAny<CancellationToken>())).ReturnsAsync((3, 2, 1, 0));
        _jobStateStore.Setup(j => j.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<ProcessingJob>());
    }

    public void Dispose()
    {
        foreach (var directory in _overlongDirectories)
        {
            try { OverlongPath.Release(directory); } catch (IOException) { /* best-effort cleanup */ } catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
        }

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort cleanup */ } catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task FailingHealthChecksAreReportedAsUnknownWithoutTheExceptionText()
    {
        var failure = new InvalidOperationException("db unreachable: sensitive-detail-7f3a");
        var healthChecks = new Mock<HealthCheckService>();
        healthChecks
            .Setup(h => h.CheckHealthAsync(It.IsAny<Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var json = Serialize(await CreateController(Settings(), healthChecks.Object).GetDiagnostics());

        var health = json.GetProperty("health");
        Assert.Equal("Unknown", health.GetProperty("status").GetString());
        Assert.Equal("Health checks could not be evaluated.", health.GetProperty("error").GetString());
        Assert.DoesNotContain("sensitive-detail-7f3a", json.GetRawText());
        // The rest of the snapshot is still produced.
        Assert.Equal(3, json.GetProperty("library").GetProperty("total").GetInt32());
        _logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            failure,
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UnsetDirectoriesAreReportedAsMissingAndLogsFallBackToTheDefaultFolder(string? unset)
    {
        var settings = Settings();
        settings.DuplicateDirectory = unset!;
        settings.ConfigDirectory = unset!;

        var json = Serialize(await CreateController(settings).GetDiagnostics());

        var storage = json.GetProperty("storage").EnumerateArray().ToList();
        foreach (var name in new[] { "Duplicates", "Config" })
        {
            var entry = storage.Single(e => e.GetProperty("name").GetString() == name);
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("path").ValueKind);
            Assert.False(entry.GetProperty("exists").GetBoolean());
            Assert.False(entry.GetProperty("writable").GetBoolean());
        }

        Assert.Equal("/Config", json.GetProperty("logs").GetProperty("directory").GetString());
    }

    [Fact]
    public async Task MissingLogDirectoryListsNoFilesAndNoError()
    {
        var settings = Settings();
        settings.ConfigDirectory = Path.Join(_root, "not-created");

        var json = Serialize(await CreateController(settings).GetDiagnostics());

        var logs = json.GetProperty("logs");
        Assert.Equal(settings.ConfigDirectory, logs.GetProperty("directory").GetString());
        Assert.Empty(logs.GetProperty("files").EnumerateArray());
        Assert.False(logs.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task LogFileThatCannotBeInspectedIsReportedAsAnErrorWithoutFailingTheSnapshot()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var settings = Settings();
        settings.ConfigDirectory = OverlongPath.CreateDirectoryHoldingAnUnreachableFile(_root, ".log");
        _overlongDirectories.Add(settings.ConfigDirectory);

        var json = Serialize(await CreateController(settings).GetDiagnostics());

        var logs = json.GetProperty("logs");
        Assert.Equal(settings.ConfigDirectory, logs.GetProperty("directory").GetString());
        Assert.Empty(logs.GetProperty("files").EnumerateArray());
        Assert.Equal("Log files could not be listed.", logs.GetProperty("error").GetString());
        Assert.Equal(3, json.GetProperty("library").GetProperty("total").GetInt32());
        _logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state!.ToString() == "Diagnostics: log files could not be listed"),
            It.Is<Exception?>(ex => ex is IOException),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task DirectoryThatRefusesNewFilesIsReportedAsNotWritable()
    {
        // procfs refuses file creation even for root, which is what makes it a
        // reliable stand-in for a read-only mount.
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc"))
        {
            return;
        }

        var settings = Settings();
        settings.WatchedDirectory = "/proc";

        var json = Serialize(await CreateController(settings).GetDiagnostics());

        var watched = json.GetProperty("storage").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "Watched");
        Assert.Equal("/proc", watched.GetProperty("path").GetString());
        Assert.True(watched.GetProperty("exists").GetBoolean());
        Assert.False(watched.GetProperty("writable").GetBoolean());
    }

    private AppSettings Settings()
    {
        var config = Path.Join(_root, "config");
        var watched = Path.Join(_root, "watched");
        var duplicates = Path.Join(_root, "duplicates");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(duplicates);
        return new AppSettings { ConfigDirectory = config, WatchedDirectory = watched, DuplicateDirectory = duplicates };
    }

    private DiagnosticsController CreateController(AppSettings settings, HealthCheckService? healthChecks = null)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(Environments.Production);
        var requestServices = new ServiceCollection().AddSingleton(environment.Object).BuildServiceProvider();

        healthChecks ??= new ServiceCollection()
            .AddLogging()
            .AddHealthChecks()
            .Services
            .BuildServiceProvider()
            .GetRequiredService<HealthCheckService>();

        var httpContext = new DefaultHttpContext { RequestServices = requestServices };
        httpContext.Items[RequestCorrelationMiddleware.ItemsKey] = "diag-cov";

        return new DiagnosticsController(
            Mock.Of<IOptionsMonitor<AppSettings>>(m => m.CurrentValue == settings),
            _fileStore.Object,
            Mock.Of<IFileWatcherService>(),
            _jobStateStore.Object,
            healthChecks,
            _logger.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    private static JsonElement Serialize(ActionResult<object> result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result.Result).Value);

    /// <summary>
    /// Builds a file that exists but cannot be reached by path on Linux: its
    /// absolute path is longer than PATH_MAX (4096 bytes), so every path-based
    /// call on it fails with ENAMETOOLONG, even for root, which ignores
    /// permission bits. Its directory can still be listed. The file name uses
    /// three-byte UTF-8 characters so the path overflows the kernel's limit in
    /// bytes while staying under 4096 characters, which .NET needs to build the
    /// path at all. The file is written under a short directory name that is
    /// lengthened afterwards, because it cannot be created at its final path;
    /// <see cref="Release"/> shortens the name again so the tree can be deleted.
    /// </summary>
    private static class OverlongPath
    {
        private const int PathMax = 4096;
        private const string ShortLeafName = "s";
        private static readonly string NestedName = new('d', 100);
        private static readonly string LeafName = new('l', 250);
        private static readonly string FileStem = new('\u65E5', 80);

        public static string CreateDirectoryHoldingAnUnreachableFile(string parent, string extension)
        {
            var fileName = FileStem + extension;
            var nested = parent;
            while (Utf8Length(nested) + 1 + LeafName.Length + 1 + Utf8Length(fileName) < PathMax)
            {
                nested = Path.Join(nested, NestedName);
            }

            var shortLeaf = Path.Join(nested, ShortLeafName);
            Directory.CreateDirectory(shortLeaf);
            File.WriteAllText(Path.Join(shortLeaf, fileName), "unreachable");

            var leaf = Path.Join(nested, LeafName);
            Directory.Move(shortLeaf, leaf);
            return leaf;
        }

        public static void Release(string leaf)
        {
            if (Directory.Exists(leaf))
            {
                Directory.Move(leaf, Path.Join(Path.GetDirectoryName(leaf)!, ShortLeafName));
            }
        }

        private static int Utf8Length(string value) => Encoding.UTF8.GetByteCount(value);
    }
}
