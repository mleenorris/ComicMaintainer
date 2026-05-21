using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ScheduledJobService"/>, the EF-backed registry
/// that persists scheduled-job definitions and merges them with the metadata
/// supplied by the registered handlers.
/// </summary>
public class ScheduledJobServiceTests
{
    private static (ScheduledJobService service, IDbContextFactory<ComicMaintainerDbContext> factory)
        BuildService(params IScheduledJobHandler[] handlers)
    {
        var dbName = $"TestDb_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        var service = new ScheduledJobService(factory, handlers, NullLogger<ScheduledJobService>.Instance);
        return (service, factory);
    }

    private sealed class FakeHandler : IScheduledJobHandler
    {
        public FakeHandler(string key, bool enabled = false, int interval = 60, string? optionsJson = null)
        {
            JobKey = key;
            Defaults = new ScheduledJobDefaults(enabled, interval, optionsJson);
        }
        public string JobKey { get; }
        public string DisplayName => JobKey;
        public string Description => "test handler";
        public ScheduledJobDefaults Defaults { get; }
        public Func<string?, CancellationToken, Task<string>> Body { get; set; } = (_, _) => Task.FromResult("ok");
        public Task<string> ExecuteAsync(string? optionsJson, CancellationToken cancellationToken) => Body(optionsJson, cancellationToken);
    }

    [Fact]
    public async Task SeedDefaultsAsync_InsertsRowsForEachRegisteredHandler()
    {
        var (service, _) = BuildService(
            new FakeHandler("alpha", enabled: true, interval: 5),
            new FakeHandler("beta"));

        await service.SeedDefaultsAsync();
        var jobs = await service.ListAsync();

        Assert.Equal(2, jobs.Count);
        var alpha = jobs.Single(j => j.JobKey == "alpha");
        Assert.True(alpha.Enabled);
        Assert.Equal(5, alpha.IntervalMinutes);
        Assert.NotNull(alpha.NextRunUtc);

        var beta = jobs.Single(j => j.JobKey == "beta");
        Assert.False(beta.Enabled);
        Assert.Null(beta.NextRunUtc); // disabled jobs don't get a next-run scheduled
    }

    [Fact]
    public async Task SeedDefaultsAsync_IsIdempotent()
    {
        var (service, _) = BuildService(new FakeHandler("alpha"));
        await service.SeedDefaultsAsync();
        await service.SeedDefaultsAsync();
        var jobs = await service.ListAsync();
        Assert.Single(jobs);
    }

    [Fact]
    public async Task UpdateAsync_PersistsChangesAndRaisesJobChanged()
    {
        var (service, _) = BuildService(new FakeHandler("alpha"));
        await service.SeedDefaultsAsync();
        var raised = false;
        service.JobChanged += (_, e) => raised = e.JobKey == "alpha";

        var updated = await service.UpdateAsync("alpha", enabled: true, intervalMinutes: 30, optionsJson: "{\"x\":1}");

        Assert.NotNull(updated);
        Assert.True(updated!.Enabled);
        Assert.Equal(30, updated.IntervalMinutes);
        Assert.Equal("{\"x\":1}", updated.OptionsJson);
        Assert.True(raised);
    }

    [Fact]
    public async Task UpdateAsync_RejectsZeroOrNegativeInterval()
    {
        var (service, _) = BuildService(new FakeHandler("alpha"));
        await service.SeedDefaultsAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.UpdateAsync("alpha", enabled: true, intervalMinutes: 0, optionsJson: null));
    }

    [Fact]
    public async Task UpdateAsync_ReturnsNullForUnknownJob()
    {
        var (service, _) = BuildService(new FakeHandler("alpha"));
        await service.SeedDefaultsAsync();
        var result = await service.UpdateAsync("unknown", enabled: true, intervalMinutes: 5, optionsJson: null);
        Assert.Null(result);
    }

    [Fact]
    public async Task RecordCompletionAsync_StoresStatusAndComputesNextRun()
    {
        var (service, _) = BuildService(new FakeHandler("alpha"));
        await service.SeedDefaultsAsync();
        await service.UpdateAsync("alpha", enabled: true, intervalMinutes: 10, optionsJson: null);

        await service.RecordCompletionAsync("alpha", ScheduledJobStatus.Success, "done", durationMs: 42);
        var job = await service.GetAsync("alpha");

        Assert.NotNull(job);
        Assert.Equal(ScheduledJobStatus.Success, job!.LastStatus);
        Assert.Equal("done", job.LastMessage);
        Assert.Equal(42, job.LastDurationMs);
        Assert.NotNull(job.LastRunUtc);
        Assert.NotNull(job.NextRunUtc);
        Assert.True(job.NextRunUtc > job.LastRunUtc);
    }

    [Fact]
    public async Task RecordCompletionAsync_WhenDisabled_ClearsNextRun()
    {
        var (service, _) = BuildService(new FakeHandler("alpha"));
        await service.SeedDefaultsAsync();
        // alpha defaults disabled
        await service.RecordCompletionAsync("alpha", ScheduledJobStatus.Success, null, durationMs: 1);
        var job = await service.GetAsync("alpha");
        Assert.NotNull(job);
        Assert.Null(job!.NextRunUtc);
    }

    [Fact]
    public void Constructor_RejectsDuplicateJobKeys()
    {
        Assert.Throws<InvalidOperationException>(() =>
            BuildService(new FakeHandler("alpha"), new FakeHandler("alpha")));
    }
}
