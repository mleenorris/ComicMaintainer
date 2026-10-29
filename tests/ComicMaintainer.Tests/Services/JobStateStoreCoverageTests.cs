using System.Globalization;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

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

    public static TheoryData<string> NumericValuesOfActiveStatuses => new()
    {
        ((int)JobStatus.Running).ToString(CultureInfo.InvariantCulture),
        ((int)JobStatus.Queued).ToString(CultureInfo.InvariantCulture)
    };

    [Theory]
    [MemberData(nameof(NumericValuesOfActiveStatuses))]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("42")]                // no such status
    [InlineData("-1")]
    [InlineData(" Running")]
    [InlineData("Running ")]
    [InlineData("running")]
    [InlineData("Queued, Running")]   // combines to Running
    [InlineData("Failed, Cancelled")] // combines to an undefined value
    public async Task GetAllAsync_TreatsAStatusThatIsNotAnExactNameAsInterrupted(string status)
    {
        await SeedAsync(new ProcessingJobEntity
        {
            JobId = Guid.NewGuid(),
            Status = status,
            OperationName = "ProcessAll",
            StartTime = DateTime.UtcNow
        });

        var stored = Assert.Single(await _store.GetAllAsync());

        Assert.Equal(JobStatus.Interrupted, stored.Status);
        Assert.True(stored.IsTerminal);
    }

    public static TheoryData<JobStatus> AllStatuses
    {
        get
        {
            var data = new TheoryData<JobStatus>();
            foreach (var status in Enum.GetValues<JobStatus>())
            {
                data.Add(status);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public async Task GetAllAsync_RestoresEveryStatusThatSaveAsyncWrites(JobStatus status)
    {
        await _store.SaveAsync(NewJob(status));

        var stored = Assert.Single(await _store.GetAllAsync());

        Assert.Equal(status, stored.Status);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("42")]
    public async Task StartupReconciliation_RestoresACorruptStatusAsAnInterruptedJobThatCanBeDeleted(string status)
    {
        // Regression test for #865: "1" was restored as a Running job that was reported as the
        // active job, and "42" as an undefined status. Neither was terminal, so neither could be
        // deleted, and with nothing running them neither could be cancelled.
        var jobId = Guid.NewGuid();
        await SeedAsync(new ProcessingJobEntity
        {
            JobId = jobId,
            Status = status,
            OperationName = "ProcessAll",
            StartTime = DateTime.UtcNow
        });
        using var processor = new ComicProcessorService(
            new TestOptionsMonitor<AppSettings>(new AppSettings()),
            NullLogger<ComicProcessorService>.Instance,
            Mock.Of<IFileStoreService>(),
            Mock.Of<IProcessingHistoryService>(),
            jobStateStore: _store);
        var reconciliation = new JobStateReconciliationHostedService(
            _store,
            processor,
            NullLogger<JobStateReconciliationHostedService>.Instance);

        await reconciliation.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(JobStatus.Interrupted, processor.GetJob(jobId)?.Status);
            Assert.Null(processor.GetActiveJob());
            Assert.True(await processor.DeleteJobAsync(jobId));
            Assert.Null(processor.GetJob(jobId));
            Assert.Empty(await _store.GetAllAsync());
        }
        finally
        {
            await reconciliation.StopAsync(CancellationToken.None);
        }
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
