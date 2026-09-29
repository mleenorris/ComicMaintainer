using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Authorization;
using ComicMaintainer.WebApi.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// One request that answers "what is this instance actually doing right now?".
///
/// The information needed to triage a report ("it's slow", "nothing is being
/// picked up", "the covers stopped loading") was previously spread across five
/// places — the watcher pill, the providers modal, the logs modal, the version
/// string and the container's own health check — and some of it (disk space,
/// uptime, database size, runtime) was not exposed at all. This endpoint
/// aggregates all of it into a single snapshot the UI renders as a diagnostics
/// panel and can copy to the clipboard for a bug report.
///
/// It exposes host paths, free disk space and process memory, so it is
/// administrator-only.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = AuthorizationPolicies.CanAdminister)]
public class DiagnosticsController : ControllerBase
{
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly IFileStoreService _fileStore;
    private readonly IFileWatcherService _watcher;
    private readonly IJobStateStore _jobStateStore;
    private readonly HealthCheckService _healthChecks;
    private readonly ILogger<DiagnosticsController> _logger;

    public DiagnosticsController(
        IOptionsMonitor<AppSettings> settings,
        IFileStoreService fileStore,
        IFileWatcherService watcher,
        IJobStateStore jobStateStore,
        HealthCheckService healthChecks,
        ILogger<DiagnosticsController> logger)
    {
        _settings = settings;
        _fileStore = fileStore;
        _watcher = watcher;
        _jobStateStore = jobStateStore;
        _healthChecks = healthChecks;
        _logger = logger;
    }

    /// <summary>
    /// Returns a point-in-time snapshot of the running instance.
    /// </summary>
    /// <remarks>
    /// Every section is gathered defensively: a diagnostics page that itself
    /// fails is worse than useless, so a section that cannot be read reports its
    /// own error instead of failing the whole request.
    /// </remarks>
    [HttpGet]
    public async Task<ActionResult<object>> GetDiagnostics(CancellationToken cancellationToken = default)
    {
        var settings = _settings.CurrentValue;
        var process = Process.GetCurrentProcess();
        var now = DateTime.UtcNow;

        var health = await GetHealthAsync(cancellationToken);

        return Ok(new
        {
            generatedAtUtc = now.ToString("O"),
            correlationId = HttpContext.GetCorrelationId(),
            application = new
            {
                version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0",
                environment = HttpContext.RequestServices
                    .GetRequiredService<IHostEnvironment>()
                    .EnvironmentName,
                startedAtUtc = process.StartTime.ToUniversalTime().ToString("O"),
                uptimeSeconds = (long)(now - process.StartTime.ToUniversalTime()).TotalSeconds
            },
            runtime = new
            {
                framework = RuntimeInformation.FrameworkDescription,
                operatingSystem = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.OSArchitecture.ToString(),
                processorCount = Environment.ProcessorCount,
                // Working set is what an operator compares against the
                // container memory limit; the managed heap tells them whether
                // growth is managed allocation or something native.
                workingSetBytes = process.WorkingSet64,
                managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
                threadCount = process.Threads.Count
            },
            health,
            watcher = GetWatcherStatus(),
            library = await GetLibraryCountsAsync(cancellationToken),
            jobs = await GetJobSummaryAsync(cancellationToken),
            storage = new[]
            {
                DescribeDirectory("Watched", settings.WatchedDirectory),
                DescribeDirectory("Duplicates", settings.DuplicateDirectory),
                DescribeDirectory("Config", settings.ConfigDirectory)
            },
            logs = DescribeLogs(settings.ConfigDirectory)
        });
    }

    /// <summary>
    /// Runs the registered health checks so the panel reports exactly what the
    /// container's readiness probe reports, rather than a second opinion.
    /// </summary>
    private async Task<object> GetHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            var report = await _healthChecks.CheckHealthAsync(cancellationToken);
            return new
            {
                status = report.Status.ToString(),
                totalDurationMs = (long)report.TotalDuration.TotalMilliseconds,
                entries = report.Entries.Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString(),
                    description = e.Value.Description,
                    durationMs = (long)e.Value.Duration.TotalMilliseconds
                }).ToArray()
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Diagnostics: health checks could not be evaluated");
            return new { status = "Unknown", error = "Health checks could not be evaluated." };
        }
    }

    private object GetWatcherStatus()
    {
        try
        {
            var settings = _settings.CurrentValue;
            return new
            {
                running = _watcher.IsRunning,
                renameEnabled = settings.WatcherEnableRename,
                normalizeEnabled = settings.WatcherEnableNormalize,
                fileStabilityDelaySeconds = settings.WatcherFileStabilityDelaySeconds
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Diagnostics: watcher status unavailable");
            return new { running = (bool?)null, error = "Watcher status unavailable." };
        }
    }

    private async Task<object> GetLibraryCountsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (total, processed, unprocessed, duplicates) = await _fileStore.GetFileCountsAsync(cancellationToken);
            return new { total, processed, unprocessed, duplicates };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Diagnostics: library counts unavailable");
            return new { error = "Library counts unavailable." };
        }
    }

    /// <summary>
    /// Summarises persisted batch jobs. "Interrupted" is called out separately
    /// because it is the one status that means work silently stopped and needs
    /// a human to restart it.
    /// </summary>
    private async Task<object> GetJobSummaryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var jobs = await _jobStateStore.GetAllAsync(cancellationToken);
            return new
            {
                total = jobs.Count,
                running = jobs.Count(j => j.Status is JobStatus.Running or JobStatus.Queued),
                interrupted = jobs.Count(j => j.Status == JobStatus.Interrupted),
                failed = jobs.Count(j => j.Status == JobStatus.Failed),
                lastStartedUtc = jobs
                    .OrderByDescending(j => j.StartTime)
                    .Select(j => (DateTime?)j.StartTime)
                    .FirstOrDefault()?
                    .ToUniversalTime()
                    .ToString("O")
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Diagnostics: job summary unavailable");
            return new { error = "Job summary unavailable." };
        }
    }

    /// <summary>
    /// Reports whether a configured directory exists, is writable and how much
    /// room is left on its volume — the three reasons a library stops updating
    /// that are invisible from inside the UI.
    /// </summary>
    private object DescribeDirectory(string name, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new { name, path = (string?)null, exists = false, writable = false };
        }

        try
        {
            var exists = Directory.Exists(path);
            long? freeBytes = null;
            long? totalBytes = null;

            if (exists)
            {
                try
                {
                    var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path)) ?? path);
                    freeBytes = drive.AvailableFreeSpace;
                    totalBytes = drive.TotalSize;
                }
                catch (Exception ex)
                {
                    // Bind mounts and overlay filesystems can refuse this; the
                    // rest of the entry is still worth reporting.
                    _logger.LogDebug(ex, "Diagnostics: free space unavailable for {Name}", name);
                }
            }

            return new
            {
                name,
                path,
                exists,
                writable = exists && IsWritable(path),
                freeBytes,
                totalBytes
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Diagnostics: directory {Name} could not be inspected", name);
            return new { name, path, exists = false, writable = false, error = "Directory could not be inspected." };
        }
    }

    /// <summary>
    /// Probes write access by actually creating and deleting a file: the
    /// permission bits alone do not account for read-only mounts, which is the
    /// common Docker misconfiguration this is meant to catch.
    /// </summary>
    private static bool IsWritable(string path)
    {
        var probe = Path.Combine(path, $".cm-write-probe-{Guid.NewGuid():N}");
        try
        {
            using (System.IO.File.Create(probe))
            {
            }

            System.IO.File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Lists the log files the logs viewer can open, with their size and age, so
    /// an operator can see at a glance whether logging has stopped or a log has
    /// rolled away the evidence.
    /// </summary>
    private object DescribeLogs(string? configDirectory)
    {
        var directory = string.IsNullOrWhiteSpace(configDirectory) ? "/Config" : configDirectory;

        try
        {
            if (!Directory.Exists(directory))
            {
                return new { directory, files = Array.Empty<object>() };
            }

            var files = Directory.GetFiles(directory, "*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(20)
                .Select(f => new
                {
                    name = f.Name,
                    sizeBytes = f.Length,
                    lastWriteUtc = f.LastWriteTimeUtc.ToString("O")
                })
                .ToArray();

            return new { directory, files };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Diagnostics: log files could not be listed");
            return new { directory, files = Array.Empty<object>(), error = "Log files could not be listed." };
        }
    }
}
