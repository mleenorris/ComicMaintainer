using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Services;

public sealed class ScheduledJobServiceCoverageTests
{
    [Fact]
    public void Handlers_AndGetHandler_ExposeTheRegisteredHandlersCaseInsensitively()
    {
        var alpha = new FakeHandler("alpha");
        var beta = new FakeHandler("beta");
        var (service, _) = BuildService(alpha, beta);

        Assert.Equal(new[] { "alpha", "beta" }, service.Handlers.Select(h => h.JobKey).OrderBy(k => k).ToArray());
        Assert.Same(alpha, service.GetHandler("alpha"));
        Assert.Same(beta, service.GetHandler("BETA"));
        Assert.Null(service.GetHandler("gamma"));
    }

    [Fact]
    public async Task SeedDefaultsAsync_RethrowsWhenTheFailedSaveLeftJobsUnseeded()
    {
        var (service, factory) = BuildService(new ThrowingSaveInterceptor(), new FakeHandler("alpha"), new FakeHandler("beta"));

        await Assert.ThrowsAsync<DbUpdateException>(() => service.SeedDefaultsAsync());

        await using var db = await factory.CreateDbContextAsync();
        Assert.Empty(db.ScheduledJobs);
    }

    [Fact]
    public async Task SeedDefaultsAsync_ToleratesLosingTheInsertRaceToAnotherWriter()
    {
        var interceptor = new ThrowingSaveInterceptor();
        var (service, factory) = BuildService(interceptor, new FakeHandler("alpha"), new FakeHandler("beta"));
        interceptor.BeforeThrow = async () =>
        {
            // Another instance seeds every job between our read and our insert.
            await using var other = await factory.CreateDbContextAsync();
            other.ScheduledJobs.Add(new ScheduledJobEntity { JobKey = "ALPHA", IntervalMinutes = 99, LastStatus = "NeverRun" });
            other.ScheduledJobs.Add(new ScheduledJobEntity { JobKey = "beta", IntervalMinutes = 99, LastStatus = "NeverRun" });
            await other.SaveChangesAsync();
        };

        await service.SeedDefaultsAsync();

        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.ScheduledJobs.OrderBy(j => j.JobKey).ToListAsync();
        Assert.Equal(new[] { "ALPHA", "beta" }, rows.Select(r => r.JobKey).ToArray());
        Assert.All(rows, r => Assert.Equal(99, r.IntervalMinutes));
    }

    [Fact]
    public async Task SeedDefaultsAsync_ClampsTheDefaultIntervalToAtLeastOneMinute()
    {
        var (service, _) = BuildService(new FakeHandler("alpha", enabled: true, interval: 0));

        var before = DateTime.UtcNow;
        await service.SeedDefaultsAsync();

        var job = await service.GetAsync("alpha");
        Assert.NotNull(job);
        Assert.Equal(1, job!.IntervalMinutes);
        Assert.Equal(ScheduledJobStatus.NeverRun, job.LastStatus);
        Assert.InRange(job.NextRunUtc!.Value, before.AddMinutes(1), DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task ListAsync_SurfacesRowsWhoseHandlerIsNoLongerRegistered()
    {
        var (service, factory) = BuildService(new FakeHandler("zeta", displayName: "Zeta job"));
        await service.SeedDefaultsAsync();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ScheduledJobs.Add(new ScheduledJobEntity
            {
                JobKey = "legacy-job",
                Enabled = true,
                IntervalMinutes = 15,
                LastStatus = "Failed",
                LastMessage = "boom"
            });
            await db.SaveChangesAsync();
        }

        var jobs = await service.ListAsync();

        Assert.Equal(new[] { "legacy-job", "Zeta job" }, jobs.Select(j => j.DisplayName).ToArray());
        var orphan = jobs[0];
        Assert.Equal("legacy-job", orphan.JobKey);
        Assert.Equal("(handler not registered)", orphan.Description);
        Assert.True(orphan.Enabled);
        Assert.Equal(15, orphan.IntervalMinutes);
        Assert.Equal(ScheduledJobStatus.Failed, orphan.LastStatus);
        Assert.Equal("boom", orphan.LastMessage);
        Assert.Equal("test handler", jobs[1].Description);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForUnknownJobsAndUnseededHandlers()
    {
        var (service, _) = BuildService(new FakeHandler("alpha"));

        Assert.Null(await service.GetAsync("unknown"));
        // Registered, but SeedDefaultsAsync has not created its row yet.
        Assert.Null(await service.GetAsync("alpha"));
    }

    [Fact]
    public async Task UpdateAsync_ReturnsNullWhenTheHandlerHasNoRowYet()
    {
        var (service, factory) = BuildService(new FakeHandler("alpha"));
        var raised = false;
        service.JobChanged += (_, _) => raised = true;

        var result = await service.UpdateAsync("alpha", enabled: true, intervalMinutes: 5, optionsJson: null);

        Assert.Null(result);
        Assert.False(raised);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Empty(db.ScheduledJobs);
    }

    [Fact]
    public async Task UpdateAsync_DisablingClearsTheNextRun()
    {
        var (service, _) = BuildService(new FakeHandler("alpha", enabled: true, interval: 5));
        await service.SeedDefaultsAsync();

        var updated = await service.UpdateAsync("alpha", enabled: false, intervalMinutes: 45, optionsJson: null);

        Assert.NotNull(updated);
        Assert.False(updated!.Enabled);
        Assert.Equal(45, updated.IntervalMinutes);
        Assert.Null(updated.NextRunUtc);
        Assert.Null((await service.GetAsync("alpha"))!.NextRunUtc);
    }

    [Fact]
    public async Task MarkRunningAsync_FlagsTheJobAsRunningAndClearsTheLastMessage()
    {
        var (service, _) = BuildService(new FakeHandler("alpha"));
        await service.SeedDefaultsAsync();
        await service.RecordCompletionAsync("alpha", ScheduledJobStatus.Failed, "previous failure", durationMs: 5);

        await service.MarkRunningAsync("alpha");

        var job = await service.GetAsync("alpha");
        Assert.Equal(ScheduledJobStatus.Running, job!.LastStatus);
        Assert.Null(job.LastMessage);
        Assert.Equal(5, job.LastDurationMs);
    }

    [Fact]
    public async Task MarkRunningAndRecordCompletion_IgnoreUnknownJobs()
    {
        var (service, factory) = BuildService(new FakeHandler("alpha"));
        await service.SeedDefaultsAsync();

        await service.MarkRunningAsync("missing");
        await service.RecordCompletionAsync("missing", ScheduledJobStatus.Success, "done", durationMs: 1);

        await using var db = await factory.CreateDbContextAsync();
        var row = Assert.Single(db.ScheduledJobs);
        Assert.Equal("alpha", row.JobKey);
        Assert.Equal(ScheduledJobStatus.NeverRun.ToString(), row.LastStatus);
    }

    [Fact]
    public async Task RecordCompletionAsync_TruncatesLongMessages()
    {
        var (service, _) = BuildService(new FakeHandler("alpha"));
        await service.SeedDefaultsAsync();

        await service.RecordCompletionAsync("alpha", ScheduledJobStatus.Failed, new string('x', 1500), durationMs: 3);

        var job = await service.GetAsync("alpha");
        Assert.Equal(new string('x', 1024), job!.LastMessage);
    }

    [Fact]
    public async Task RecordCompletionAsync_KeepsAMessageOfExactlyTheLimit()
    {
        var (service, _) = BuildService(new FakeHandler("alpha"));
        await service.SeedDefaultsAsync();

        await service.RecordCompletionAsync("alpha", ScheduledJobStatus.Success, new string('y', 1024), durationMs: 3);

        Assert.Equal(new string('y', 1024), (await service.GetAsync("alpha"))!.LastMessage);
    }

    private static (ScheduledJobService Service, IDbContextFactory<ComicMaintainerDbContext> Factory) BuildService(
        params IScheduledJobHandler[] handlers)
        => BuildService(interceptor: null, handlers);

    private static (ScheduledJobService Service, IDbContextFactory<ComicMaintainerDbContext> Factory) BuildService(
        IInterceptor? interceptor,
        params IScheduledJobHandler[] handlers)
    {
        var dbName = $"ScheduledJobCoverage_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(options =>
        {
            options.UseInMemoryDatabase(dbName);
            if (interceptor is not null)
            {
                options.AddInterceptors(interceptor);
            }
        });
        var factory = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        return (new ScheduledJobService(factory, handlers, NullLogger<ScheduledJobService>.Instance), factory);
    }

    private sealed class FakeHandler : IScheduledJobHandler
    {
        public FakeHandler(string key, bool enabled = false, int interval = 60, string? displayName = null)
        {
            JobKey = key;
            DisplayName = displayName ?? key;
            Defaults = new ScheduledJobDefaults(enabled, interval);
        }

        public string JobKey { get; }

        public string DisplayName { get; }

        public string Description => "test handler";

        public ScheduledJobDefaults Defaults { get; }

        public Task<string> ExecuteAsync(string? optionsJson, CancellationToken cancellationToken) => Task.FromResult("ok");
    }

    /// <summary>
    /// Fails every save as if a unique constraint had been violated, optionally
    /// running a competing write first. Saves made by that competing write pass through.
    /// </summary>
    private sealed class ThrowingSaveInterceptor : SaveChangesInterceptor
    {
        private bool _inCompetingWrite;

        public Func<Task>? BeforeThrow { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_inCompetingWrite)
            {
                return result;
            }

            if (BeforeThrow is not null)
            {
                _inCompetingWrite = true;
                try
                {
                    await BeforeThrow();
                }
                finally
                {
                    _inCompetingWrite = false;
                }
            }

            throw new DbUpdateException("simulated constraint failure");
        }
    }
}
