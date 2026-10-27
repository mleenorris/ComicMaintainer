using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Services;

public class JobStateStoreCoverageTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _factory = null!;
    private JobStateStore _store = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        _factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        _store = new JobStateStore(_factory, NullLogger<JobStateStore>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static ProcessingJob NewJob(JobStatus status = JobStatus.Completed) => new()
    {
        JobId = Guid.NewGuid(),
        Status = status,
        OperationName = "ProcessAll",
        Files = new List<string> { "/lib/a.cbz" },
        TotalFiles = 1,
        StartTime = DateTime.UtcNow
    };

    private async Task SeedAsync(ProcessingJobEntity entity)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.ProcessingJobs.Add(entity);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task PruneAsync_RejectsANegativeRetentionCount()
    {
        await _store.SaveAsync(NewJob());

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _store.PruneAsync(DateTime.UtcNow.AddDays(1), keepMostRecent: -1));

        Assert.Equal("keepMostRecent", ex.ParamName);
        Assert.Single(await _store.GetAllAsync());
    }

    [Fact]
    public async Task SaveAsync_StoresAnEmptyOperationNameAsEmpty()
    {
        var job = NewJob();
        job.OperationName = string.Empty;

        await _store.SaveAsync(job);

        var stored = Assert.Single(await _store.GetAllAsync());
        Assert.Equal(string.Empty, stored.OperationName);
    }

    [Fact]
    public async Task SaveAsync_TruncatesTextThatExceedsTheColumnLimits()
    {
        var longOperation = string.Concat(Enumerable.Repeat("0123456789", 20));
        var longCurrentFile = "/lib/" + new string('a', 3000);
        var job = NewJob(JobStatus.Running);
        job.OperationName = longOperation;
        job.CurrentFile = longCurrentFile;

        await _store.SaveAsync(job);

        var stored = Assert.Single(await _store.GetAllAsync());
        Assert.Equal(longOperation[..128], stored.OperationName);
        Assert.Equal(longCurrentFile[..2048], stored.CurrentFile);
    }

    [Fact]
    public async Task MarkInterruptedAsync_UsesTheCurrentTimeWhenNoProgressWriteWasRecorded()
    {
        var jobId = Guid.NewGuid();
        await SeedAsync(new ProcessingJobEntity
        {
            JobId = jobId,
            Status = nameof(JobStatus.Running),
            OperationName = "ProcessAll",
            StartTime = DateTime.UtcNow.AddHours(-1),
            UpdatedAt = default
        });
        var before = DateTime.UtcNow;

        var interrupted = Assert.Single(await _store.MarkInterruptedAsync());

        var after = DateTime.UtcNow;
        Assert.Equal(jobId, interrupted.JobId);
        Assert.Equal(JobStatus.Interrupted, interrupted.Status);
        Assert.NotNull(interrupted.EndTime);
        Assert.InRange(interrupted.EndTime!.Value, before, after);
    }

    [Fact]
    public async Task GetAllAsync_TreatsAnUnrecognisedStatusAsInterrupted()
    {
        await SeedAsync(new ProcessingJobEntity
        {
            JobId = Guid.NewGuid(),
            Status = "Exploded",
            OperationName = "ProcessAll",
            StartTime = DateTime.UtcNow
        });

        var stored = Assert.Single(await _store.GetAllAsync());

        Assert.Equal(JobStatus.Interrupted, stored.Status);
        Assert.True(stored.IsTerminal);
    }

    [Theory]
    [InlineData("", "   ")]
    [InlineData("null", "null")]
    public async Task GetAllAsync_TreatsMissingPayloadsAsEmptyCollections(string filesJson, string errorsJson)
    {
        await SeedAsync(new ProcessingJobEntity
        {
            JobId = Guid.NewGuid(),
            Status = nameof(JobStatus.Completed),
            OperationName = "ProcessAll",
            FilesJson = filesJson,
            ErrorsJson = errorsJson,
            StartTime = DateTime.UtcNow
        });

        var stored = Assert.Single(await _store.GetAllAsync());

        Assert.NotNull(stored.Files);
        Assert.Empty(stored.Files);
        Assert.NotNull(stored.Errors);
        Assert.Empty(stored.Errors);
        Assert.Equal(JobStatus.Completed, stored.Status);
    }
}
