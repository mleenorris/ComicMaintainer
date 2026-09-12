using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers durable batch job state, which lets the server report a definite outcome for jobs
/// that were in flight when the process stopped.
/// </summary>
public class JobStateStoreTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ServiceProvider _provider = null!;
    private JobStateStore _store = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        var factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        _store = new JobStateStore(factory, new Mock<ILogger<JobStateStore>>().Object);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static ProcessingJob NewJob(
        JobStatus status = JobStatus.Running,
        string operation = "ProcessAll",
        DateTime? startTime = null)
    {
        return new ProcessingJob
        {
            JobId = Guid.NewGuid(),
            Status = status,
            OperationName = operation,
            Files = new List<string> { "/lib/a.cbz", "/lib/b.cbz" },
            TotalFiles = 2,
            ProcessedFiles = 1,
            StartTime = startTime ?? DateTime.UtcNow
        };
    }

    [Fact]
    public async Task SaveAsync_RoundTripsAllJobState()
    {
        var job = NewJob();
        job.CurrentFile = "/lib/b.cbz";
        job.Errors["/lib/a.cbz"] = "boom";
        job.FailedFiles = 1;

        await _store.SaveAsync(job);

        var stored = Assert.Single(await _store.GetAllAsync());
        Assert.Equal(job.JobId, stored.JobId);
        Assert.Equal(JobStatus.Running, stored.Status);
        Assert.Equal("ProcessAll", stored.OperationName);
        Assert.Equal(job.Files, stored.Files);
        Assert.Equal("boom", stored.Errors["/lib/a.cbz"]);
        Assert.Equal(1, stored.ProcessedFiles);
        Assert.Equal(1, stored.FailedFiles);
        Assert.Equal("/lib/b.cbz", stored.CurrentFile);
    }

    [Fact]
    public async Task SaveAsync_UpdatesExistingRecordRatherThanDuplicating()
    {
        var job = NewJob();
        await _store.SaveAsync(job);

        job.Status = JobStatus.Completed;
        job.ProcessedFiles = 2;
        await _store.SaveAsync(job);

        var stored = Assert.Single(await _store.GetAllAsync());
        Assert.Equal(JobStatus.Completed, stored.Status);
        Assert.Equal(2, stored.ProcessedFiles);
    }

    [Fact]
    public async Task MarkInterruptedAsync_ConvertsOnlyNonTerminalJobs()
    {
        var running = NewJob(JobStatus.Running);
        var queued = NewJob(JobStatus.Queued);
        var completed = NewJob(JobStatus.Completed);
        var cancelled = NewJob(JobStatus.Cancelled);

        foreach (var job in new[] { running, queued, completed, cancelled })
        {
            await _store.SaveAsync(job);
        }

        var interrupted = await _store.MarkInterruptedAsync();

        Assert.Equal(2, interrupted.Count);
        Assert.Contains(interrupted, j => j.JobId == running.JobId);
        Assert.Contains(interrupted, j => j.JobId == queued.JobId);

        var all = (await _store.GetAllAsync()).ToDictionary(j => j.JobId);
        Assert.Equal(JobStatus.Interrupted, all[running.JobId].Status);
        Assert.Equal(JobStatus.Interrupted, all[queued.JobId].Status);
        Assert.Equal(JobStatus.Completed, all[completed.JobId].Status);
        Assert.Equal(JobStatus.Cancelled, all[cancelled.JobId].Status);
    }

    [Fact]
    public async Task MarkInterruptedAsync_SetsEndTimeAndPreservesProgress()
    {
        var job = NewJob(JobStatus.Running);
        job.ProcessedFiles = 1;
        await _store.SaveAsync(job);

        var interrupted = Assert.Single(await _store.MarkInterruptedAsync());

        Assert.NotNull(interrupted.EndTime);
        // The recorded progress is what tells the user how much of the batch actually ran.
        Assert.Equal(1, interrupted.ProcessedFiles);
        Assert.Equal(2, interrupted.TotalFiles);
        Assert.True(interrupted.IsTerminal);
    }

    [Fact]
    public async Task MarkInterruptedAsync_IsSafeWhenNothingWasInFlight()
    {
        await _store.SaveAsync(NewJob(JobStatus.Completed));

        Assert.Empty(await _store.MarkInterruptedAsync());
    }

    [Fact]
    public async Task DeleteAsync_RemovesRecordSoItDoesNotReturnAfterRestart()
    {
        var job = NewJob(JobStatus.Completed);
        await _store.SaveAsync(job);

        Assert.True(await _store.DeleteAsync(job.JobId));
        Assert.Empty(await _store.GetAllAsync());
        Assert.False(await _store.DeleteAsync(job.JobId));
    }

    [Fact]
    public async Task PruneAsync_RemovesOldTerminalJobsButKeepsRecentOnes()
    {
        var old = NewJob(JobStatus.Completed, startTime: DateTime.UtcNow.AddDays(-30));
        var recent = NewJob(JobStatus.Completed, startTime: DateTime.UtcNow);

        await _store.SaveAsync(old);
        await _store.SaveAsync(recent);

        var deleted = await _store.PruneAsync(DateTime.UtcNow.AddDays(-7), keepMostRecent: 0);

        Assert.Equal(1, deleted);
        var remaining = Assert.Single(await _store.GetAllAsync());
        Assert.Equal(recent.JobId, remaining.JobId);
    }

    [Fact]
    public async Task PruneAsync_KeepsMostRecentJobsRegardlessOfAge()
    {
        for (var i = 0; i < 5; i++)
        {
            await _store.SaveAsync(NewJob(JobStatus.Completed, startTime: DateTime.UtcNow.AddDays(-30 - i)));
        }

        var deleted = await _store.PruneAsync(DateTime.UtcNow.AddDays(-7), keepMostRecent: 3);

        Assert.Equal(2, deleted);
        Assert.Equal(3, (await _store.GetAllAsync()).Count);
    }

    [Fact]
    public async Task PruneAsync_NeverRemovesAJobThatIsStillInFlight()
    {
        // An in-flight job must survive pruning however old it looks, or a long-running batch
        // would lose the record it is still writing to.
        var running = NewJob(JobStatus.Running, startTime: DateTime.UtcNow.AddDays(-30));
        await _store.SaveAsync(running);

        var deleted = await _store.PruneAsync(DateTime.UtcNow.AddDays(-7), keepMostRecent: 0);

        Assert.Equal(0, deleted);
        Assert.Single(await _store.GetAllAsync());
    }

    [Fact]
    public async Task GetAllAsync_ReturnsMostRecentlyStartedFirst()
    {
        var older = NewJob(JobStatus.Completed, startTime: DateTime.UtcNow.AddHours(-2));
        var newer = NewJob(JobStatus.Completed, startTime: DateTime.UtcNow);

        await _store.SaveAsync(older);
        await _store.SaveAsync(newer);

        var all = await _store.GetAllAsync();

        Assert.Equal(newer.JobId, all[0].JobId);
        Assert.Equal(older.JobId, all[1].JobId);
    }

    [Fact]
    public async Task GetAllAsync_TreatsCorruptPayloadAsEmptyInsteadOfFailing()
    {
        var job = NewJob(JobStatus.Completed);
        await _store.SaveAsync(job);

        var factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var entity = await db.ProcessingJobs.FirstAsync(e => e.JobId == job.JobId);
            entity.FilesJson = "{not json";
            await db.SaveChangesAsync();
        }

        // One unreadable record must not break listing for every other job.
        var stored = Assert.Single(await _store.GetAllAsync());
        Assert.Empty(stored.Files);
        Assert.Equal(JobStatus.Completed, stored.Status);
    }
}
