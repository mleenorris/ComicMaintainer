using System.Text.Json;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.WebApi.Controllers;
using ComicMaintainer.WebApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// Exercises <see cref="ScheduledJobsController"/> against the real EF-backed
/// <see cref="ScheduledJobService"/> (in-memory database) and a real
/// <see cref="ScheduledJobsHostedService"/>, so list/update/run/findings are
/// verified end-to-end through the persisted state.
/// </summary>
public sealed class ScheduledJobsControllerCoverageTests : IAsyncLifetime
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(30);

    private readonly ServiceProvider _provider;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _factory;
    private readonly FakeHandler _audit = new("metadata-audit", "Metadata audit", enabled: true, interval: 1440, optionsJson: "{\"autoCorrect\":false}");
    private readonly FakeHandler _backup = new("database-backup", "Database backup", enabled: false, interval: 60);
    private readonly ScheduledJobService _service;
    private readonly SignalingLogger<ScheduledJobsHostedService> _hostedLogger = new();
    private readonly ScheduledJobsHostedService _hostedService;
    private readonly ScheduledJobsController _controller;

    public ScheduledJobsControllerCoverageTests()
    {
        var dbName = $"ScheduledJobsController_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseInMemoryDatabase(dbName));
        _provider = services.BuildServiceProvider();
        _factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();

        _service = new ScheduledJobService(_factory, new IScheduledJobHandler[] { _audit, _backup }, NullLogger<ScheduledJobService>.Instance);
        _hostedService = new ScheduledJobsHostedService(_service, _hostedLogger);
        _controller = new ScheduledJobsController(_service, _hostedService, _factory, NullLogger<ScheduledJobsController>.Instance);
    }

    public Task InitializeAsync() => _service.SeedDefaultsAsync();

    // The hosted service is deliberately not disposed: it was never started (no timers),
    // and a background "run now" may still be releasing its single-flight semaphore.
    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);

    // ----- List / Get -----------------------------------------------------

    [Fact]
    public async Task List_ReturnsEveryJobOrderedByDisplayNameWithItsSchedule()
    {
        var result = await _controller.List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var jobs = Json(ok.Value).EnumerateArray().ToList();
        Assert.Equal(new[] { "database-backup", "metadata-audit" }, jobs.Select(j => j.GetProperty("jobKey").GetString()));

        var audit = jobs[1];
        Assert.Equal("Metadata audit", audit.GetProperty("displayName").GetString());
        Assert.Equal("Metadata audit description", audit.GetProperty("description").GetString());
        Assert.True(audit.GetProperty("enabled").GetBoolean());
        Assert.Equal(1440, audit.GetProperty("intervalMinutes").GetInt32());
        Assert.Equal("NeverRun", audit.GetProperty("lastStatus").GetString());
        Assert.Equal(JsonValueKind.Null, audit.GetProperty("lastRunUtc").ValueKind);
        Assert.Equal(JsonValueKind.String, audit.GetProperty("nextRunUtc").ValueKind);
        Assert.Equal("{\"autoCorrect\":false}", audit.GetProperty("optionsJson").GetString());

        var backup = jobs[0];
        Assert.False(backup.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, backup.GetProperty("nextRunUtc").ValueKind);
    }

    [Fact]
    public async Task Get_KnownJob_ReturnsIt()
    {
        var result = await _controller.Get("metadata-audit", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("metadata-audit", Json(ok.Value).GetProperty("jobKey").GetString());
    }

    [Fact]
    public async Task Get_UnknownJob_ReturnsNotFound()
    {
        var result = await _controller.Get("no-such-job", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ----- Update ---------------------------------------------------------

    [Fact]
    public async Task Update_WithoutBody_ReturnsBadRequest()
    {
        var result = await _controller.Update("metadata-audit", null!, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Body required.", Json(badRequest.Value).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Update_EnabledWithNonPositiveInterval_ReturnsBadRequestAndKeepsSchedule(int interval)
    {
        var request = new ScheduledJobsController.UpdateScheduledJobRequest { Enabled = true, IntervalMinutes = interval };

        var result = await _controller.Update("metadata-audit", request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("intervalMinutes must be a positive number when enabled.", Json(badRequest.Value).GetProperty("error").GetString());
        var stored = await _service.GetAsync("metadata-audit");
        Assert.Equal(1440, stored!.IntervalMinutes);
    }

    [Fact]
    public async Task Update_EnablesJobAndPersistsIntervalAndOptions()
    {
        var request = new ScheduledJobsController.UpdateScheduledJobRequest
        {
            Enabled = true,
            IntervalMinutes = 30,
            OptionsJson = "{\"retention\":7}",
        };

        var result = await _controller.Update("database-backup", request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Json(ok.Value);
        Assert.True(dto.GetProperty("enabled").GetBoolean());
        Assert.Equal(30, dto.GetProperty("intervalMinutes").GetInt32());
        Assert.Equal("{\"retention\":7}", dto.GetProperty("optionsJson").GetString());

        var stored = await _service.GetAsync("database-backup");
        Assert.True(stored!.Enabled);
        Assert.Equal(30, stored.IntervalMinutes);
        Assert.Equal("{\"retention\":7}", stored.OptionsJson);
        Assert.NotNull(stored.NextRunUtc);
    }

    [Fact]
    public async Task Update_DisablingWithoutInterval_StoresMinimumIntervalAndClearsNextRun()
    {
        var request = new ScheduledJobsController.UpdateScheduledJobRequest { Enabled = false, IntervalMinutes = 0 };

        var result = await _controller.Update("metadata-audit", request, CancellationToken.None);

        var dto = Json(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(dto.GetProperty("enabled").GetBoolean());
        Assert.Equal(1, dto.GetProperty("intervalMinutes").GetInt32());
        var stored = await _service.GetAsync("metadata-audit");
        Assert.False(stored!.Enabled);
        Assert.Equal(1, stored.IntervalMinutes);
        Assert.Null(stored.NextRunUtc);
    }

    [Fact]
    public async Task Update_UnknownJob_ReturnsNotFound()
    {
        var request = new ScheduledJobsController.UpdateScheduledJobRequest { Enabled = true, IntervalMinutes = 10 };

        var result = await _controller.Update("no-such-job", request, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Update_WhenServiceRejectsInterval_ReturnsBadRequestWithReason()
    {
        var service = new Mock<IScheduledJobService>();
        service
            .Setup(s => s.UpdateAsync("metadata-audit", true, 5, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentOutOfRangeException("intervalMinutes", "Interval exceeds the maximum."));
        var controller = new ScheduledJobsController(service.Object, _hostedService, _factory, NullLogger<ScheduledJobsController>.Instance);

        var result = await controller.Update(
            "metadata-audit",
            new ScheduledJobsController.UpdateScheduledJobRequest { Enabled = true, IntervalMinutes = 5 },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.StartsWith("Interval exceeds the maximum.", Json(badRequest.Value).GetProperty("error").GetString());
    }

    // ----- Run ------------------------------------------------------------

    [Fact]
    public async Task Run_UnknownJob_ReturnsNotFoundWithoutRunningAnything()
    {
        var result = await _controller.Run("no-such-job", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.False(_audit.Executed.Task.IsCompleted);
        Assert.False(_backup.Executed.Task.IsCompleted);
    }

    [Fact]
    public async Task Run_KnownJob_QueuesImmediateRunWithSavedOptions()
    {
        var completed = _hostedLogger.WaitFor("completed");

        var result = await _controller.Run("metadata-audit", CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        var body = Json(accepted.Value);
        Assert.Equal("metadata-audit", body.GetProperty("jobKey").GetString());
        Assert.True(body.GetProperty("queued").GetBoolean());

        // The run happens in the background; wait for the handler and the recorded outcome.
        Assert.Equal("{\"autoCorrect\":false}", await _audit.Executed.Task.WaitAsync(SignalTimeout));
        await completed.WaitAsync(SignalTimeout);
        var stored = await _service.GetAsync("metadata-audit");
        Assert.Equal(ScheduledJobStatus.Success, stored!.LastStatus);
        Assert.Equal("audited", stored.LastMessage);
        Assert.False(_backup.Executed.Task.IsCompleted);
    }

    // ----- Metadata audit findings ---------------------------------------

    private async Task SeedFindingsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.MetadataAuditFindings.AddRange(
            new MetadataAuditFindingEntity
            {
                FilePath = "/library/c.cbz",
                FindingType = nameof(MetadataAuditFindingType.SeriesMismatch),
                ExpectedSeries = "Saga",
                ActualSeries = "Sage",
                ActualIssue = "3",
                Details = "series differs",
                DetectedAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            },
            new MetadataAuditFindingEntity { FilePath = "/library/a.cbz", FindingType = nameof(MetadataAuditFindingType.MissingChapter) },
            new MetadataAuditFindingEntity { FilePath = "/library/d.cbz", FindingType = nameof(MetadataAuditFindingType.MissingChapter) },
            new MetadataAuditFindingEntity { FilePath = "/library/b.cbz", FindingType = nameof(MetadataAuditFindingType.Unreadable) });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetMetadataAuditFindings_WithoutFilter_ReturnsAllOrderedByPathWithDefaultPaging()
    {
        await SeedFindingsAsync();

        var result = await _controller.GetMetadataAuditFindings(type: null);

        var page = Json(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(4, page.GetProperty("total").GetInt32());
        Assert.Equal(0, page.GetProperty("offset").GetInt32());
        Assert.Equal(200, page.GetProperty("limit").GetInt32());
        var findings = page.GetProperty("findings").EnumerateArray().ToList();
        Assert.Equal(
            new[] { "/library/a.cbz", "/library/b.cbz", "/library/c.cbz", "/library/d.cbz" },
            findings.Select(f => f.GetProperty("filePath").GetString()));

        var mismatch = findings[2];
        Assert.True(mismatch.GetProperty("id").GetInt32() > 0);
        Assert.Equal("SeriesMismatch", mismatch.GetProperty("findingType").GetString());
        Assert.Equal("Saga", mismatch.GetProperty("expectedSeries").GetString());
        Assert.Equal("Sage", mismatch.GetProperty("actualSeries").GetString());
        Assert.Equal("3", mismatch.GetProperty("actualIssue").GetString());
        Assert.Equal("series differs", mismatch.GetProperty("details").GetString());
        Assert.Equal("2026-01-02T03:04:05Z", mismatch.GetProperty("detectedAtUtc").GetString());
    }

    [Fact]
    public async Task GetMetadataAuditFindings_FiltersByTypeAndPages()
    {
        await SeedFindingsAsync();

        var result = await _controller.GetMetadataAuditFindings("MissingChapter", offset: 1, limit: 1);

        var page = Json(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.Equal(1, page.GetProperty("offset").GetInt32());
        Assert.Equal(1, page.GetProperty("limit").GetInt32());
        var finding = Assert.Single(page.GetProperty("findings").EnumerateArray());
        Assert.Equal("/library/d.cbz", finding.GetProperty("filePath").GetString());
    }

    [Theory]
    [InlineData("   ", -10, 0)]
    [InlineData("", -1, 1001)]
    [InlineData(null, 0, -3)]
    public async Task GetMetadataAuditFindings_ClampsInvalidPagingAndIgnoresBlankType(string? type, int offset, int limit)
    {
        await SeedFindingsAsync();

        var result = await _controller.GetMetadataAuditFindings(type, offset, limit);

        var page = Json(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(4, page.GetProperty("total").GetInt32());
        Assert.Equal(0, page.GetProperty("offset").GetInt32());
        Assert.Equal(200, page.GetProperty("limit").GetInt32());
        Assert.Equal(4, page.GetProperty("findings").GetArrayLength());
    }

    [Fact]
    public async Task GetMetadataAuditFindings_WithNoFindings_ReturnsEmptyPage()
    {
        var result = await _controller.GetMetadataAuditFindings("Unreadable", offset: 0, limit: 1000);

        var page = Json(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(0, page.GetProperty("total").GetInt32());
        Assert.Equal(1000, page.GetProperty("limit").GetInt32());
        Assert.Equal(0, page.GetProperty("findings").GetArrayLength());
    }

    private sealed class FakeHandler : IScheduledJobHandler
    {
        public FakeHandler(string key, string displayName, bool enabled, int interval, string? optionsJson = null)
        {
            JobKey = key;
            DisplayName = displayName;
            Defaults = new ScheduledJobDefaults(enabled, interval, optionsJson);
        }

        public string JobKey { get; }
        public string DisplayName { get; }
        public string Description => DisplayName + " description";
        public ScheduledJobDefaults Defaults { get; }

        /// <summary>Completed with the options the run was started with.</summary>
        public TaskCompletionSource<string?> Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> ExecuteAsync(string? optionsJson, CancellationToken cancellationToken)
        {
            Executed.TrySetResult(optionsJson);
            return Task.FromResult("audited");
        }
    }

    /// <summary>Logger that lets a test await a log message containing a given fragment.</summary>
    private sealed class SignalingLogger<T> : ILogger<T>
    {
        private readonly List<(string Fragment, TaskCompletionSource Signal)> _waiters = new();

        public Task WaitFor(string fragment)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waiters)
            {
                _waiters.Add((fragment, signal));
            }
            return signal.Task;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (_waiters)
            {
                foreach (var (fragment, signal) in _waiters)
                {
                    if (message.Contains(fragment, StringComparison.Ordinal))
                    {
                        signal.TrySetResult();
                    }
                }
            }
        }
    }
}
