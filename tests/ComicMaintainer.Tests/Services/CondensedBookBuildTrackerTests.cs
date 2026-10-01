using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Guards the status monitoring for condensed EPUB creation: a large book is
/// built outside the request that asked for it, so its progress, its failure
/// reason and its finished file must all be observable afterwards.
/// </summary>
public class CondensedBookBuildTrackerTests : IDisposable
{
    private const string Owner = "user-1";

    private readonly string _workDir;
    private readonly Mock<IComicEmailService> _email = new(MockBehavior.Strict);
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private readonly CondensedBookBuildTracker _tracker;

    public CondensedBookBuildTrackerTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"condense-build-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);

        _tracker = new CondensedBookBuildTracker(
            _email.Object,
            new Mock<ILogger<CondensedBookBuildTracker>>().Object,
            _clock,
            TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task StartAsync_CompletesTheBuildAndKeepsTheFile()
    {
        SetupPlan();
        var epub = CreateBook("Series 001-002");
        SetupBuild((_, _) => Task.FromResult(new CondensedBookFile(epub, Path.GetFileName(epub))));

        var started = await _tracker.StartAsync(Request(), Owner);
        Assert.Equal(CondensedBookBuildStatus.Queued, started.Status);
        Assert.Equal("Series 001-002", started.DisplayName);

        var finished = await WaitForTerminalAsync(started.BuildId);
        Assert.Equal(CondensedBookBuildStatus.Completed, finished.Status);
        Assert.Null(finished.Error);
        Assert.Equal(new FileInfo(epub).Length, finished.FileSizeBytes);
        Assert.NotNull(finished.CompletedAt);

        // The file survives the build so the download can be retried.
        var file = _tracker.GetCompletedFile(started.BuildId, Owner);
        Assert.NotNull(file);
        Assert.True(File.Exists(file!.FilePath));
    }

    [Fact]
    public async Task StartAsync_SurfacesTheConversionProgress()
    {
        SetupPlan(issueCount: 2);
        var epub = CreateBook("Series 001-002");
        var released = new TaskCompletionSource();
        var reported = new TaskCompletionSource();

        SetupBuild(async (progress, token) =>
        {
            progress!.Report(new EpubConversionProgress(
                EpubConversionPhase.Writing, 3, 10, 1, 2, "Series - Chapter 0002.cbz", 1, 1));
            reported.SetResult();
            await released.Task.WaitAsync(token);
            return new CondensedBookFile(epub, Path.GetFileName(epub));
        });

        var started = await _tracker.StartAsync(Request(), Owner);
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var running = await WaitForAsync(started.BuildId, b => b.Progress.CompletedPages == 3);
        Assert.Equal(CondensedBookBuildStatus.Running, running.Status);
        Assert.Equal("writing", running.Progress.Phase);
        Assert.Equal(10, running.Progress.TotalPages);
        Assert.Equal(30, running.Progress.Percentage);
        Assert.Equal("Series - Chapter 0002.cbz", running.Progress.CurrentIssue);

        released.SetResult();
        await WaitForTerminalAsync(started.BuildId);
    }

    [Fact]
    public async Task StartAsync_RecordsWhyABuildFailed()
    {
        SetupPlan();
        SetupBuild((_, _) => Task.FromException<CondensedBookFile>(
            new InvalidOperationException("Archive contains no page images")));

        var started = await _tracker.StartAsync(Request(), Owner);

        var finished = await WaitForTerminalAsync(started.BuildId);
        Assert.Equal(CondensedBookBuildStatus.Failed, finished.Status);
        Assert.Equal("Archive contains no page images", finished.Error);
        Assert.Null(_tracker.GetCompletedFile(started.BuildId, Owner));
    }

    [Fact]
    public async Task StartAsync_RejectsABookIndexThePlanDoesNotProduce()
    {
        SetupPlan();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _tracker.StartAsync(Request(bookIndex: 5), Owner));
    }

    [Fact]
    public async Task StartAsync_JoinsAnIdenticalBuildThatIsAlreadyRunning()
    {
        SetupPlan();
        var epub = CreateBook("Series 001-002");
        var released = new TaskCompletionSource();
        SetupBuild(async (_, token) =>
        {
            await released.Task.WaitAsync(token);
            return new CondensedBookFile(epub, Path.GetFileName(epub));
        });

        var first = await _tracker.StartAsync(Request(), Owner);
        var second = await _tracker.StartAsync(Request(), Owner);

        Assert.Equal(first.BuildId, second.BuildId);
        Assert.Single(_tracker.List(Owner));

        released.SetResult();
        await WaitForTerminalAsync(first.BuildId);
    }

    [Fact]
    public async Task StartAsync_SimultaneousIdenticalRequestsRegisterOnlyOneBuild()
    {
        const int requestCount = 8;
        var allPlansStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePlans = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var planned = 0;
        SetupPlan(beforeReturn: async () =>
        {
            if (Interlocked.Increment(ref planned) == requestCount)
            {
                allPlansStarted.SetResult();
            }

            await releasePlans.Task;
        });
        var epub = CreateBook("Series 001-002");
        var releaseBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupBuild(async (_, token) =>
        {
            await releaseBuild.Task.WaitAsync(token);
            return new CondensedBookFile(epub, Path.GetFileName(epub));
        });

        var requests = Enumerable.Range(0, requestCount)
            .Select(_ => Task.Run(() => _tracker.StartAsync(Request(), Owner)))
            .ToArray();
        await allPlansStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        releasePlans.SetResult();

        var started = await Task.WhenAll(requests);

        Assert.All(started, build => Assert.Equal(started[0].BuildId, build.BuildId));
        _email.Verify(e => e.CreateCondensedBookAsync(
            It.IsAny<IEnumerable<string>>(),
            It.IsAny<string?>(),
            It.IsAny<int?>(),
            It.IsAny<int>(),
            It.IsAny<bool>(),
            It.IsAny<int?>(),
            It.IsAny<bool>(),
            It.IsAny<IProgress<EpubConversionProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        releaseBuild.SetResult();
        await WaitForTerminalAsync(started[0].BuildId);
    }

    [Fact]
    public async Task Cancel_StopsARunningBuild()
    {
        SetupPlan();
        SetupBuild(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new CondensedBookFile("unreachable", "unreachable");
        });

        var started = await _tracker.StartAsync(Request(), Owner);
        await WaitForAsync(started.BuildId, b => b.Status == CondensedBookBuildStatus.Running);

        Assert.True(_tracker.Cancel(started.BuildId, Owner));

        var finished = await WaitForTerminalAsync(started.BuildId);
        Assert.Equal(CondensedBookBuildStatus.Cancelled, finished.Status);

        // Nothing is left running, so a later build is not blocked.
        Assert.False(_tracker.Cancel(started.BuildId, Owner));
    }

    [Fact]
    public async Task Dispose_WaitsForRunningBuildWorkersBeforeReturning()
    {
        SetupPlan();
        var epub = CreateBook("Series 001-002");
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupBuild(async (_, _) =>
        {
            buildStarted.SetResult();
            await releaseBuild.Task;
            return new CondensedBookFile(epub, Path.GetFileName(epub));
        });

        await _tracker.StartAsync(Request(), Owner);
        await buildStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var disposal = Task.Run(_tracker.Dispose);
        try
        {
            await Task.Delay(50);
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            releaseBuild.SetResult();
        }

        await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(epub));
    }

    [Fact]
    public async Task Discard_ForgetsTheBuildAndDeletesItsFile()
    {
        SetupPlan();
        var epub = CreateBook("Series 001-002");
        SetupBuild((_, _) => Task.FromResult(new CondensedBookFile(epub, Path.GetFileName(epub))));

        var started = await _tracker.StartAsync(Request(), Owner);
        await WaitForTerminalAsync(started.BuildId);

        Assert.True(_tracker.Discard(started.BuildId, Owner));
        Assert.Null(_tracker.Get(started.BuildId, Owner));
        Assert.False(File.Exists(epub));
    }

    [Fact]
    public async Task FinishedBuildsAreDroppedOnceTheyExpire()
    {
        SetupPlan();
        var epub = CreateBook("Series 001-002");
        SetupBuild((_, _) => Task.FromResult(new CondensedBookFile(epub, Path.GetFileName(epub))));

        var started = await _tracker.StartAsync(Request(), Owner);
        await WaitForTerminalAsync(started.BuildId);

        _clock.Advance(TimeSpan.FromMinutes(31));

        Assert.Null(_tracker.Get(started.BuildId, Owner));
        Assert.False(File.Exists(epub));
    }

    [Fact]
    public async Task BuildsAreNotVisibleToAnotherUser()
    {
        SetupPlan();
        var epub = CreateBook("Series 001-002");
        SetupBuild((_, _) => Task.FromResult(new CondensedBookFile(epub, Path.GetFileName(epub))));

        var started = await _tracker.StartAsync(Request(), Owner);
        await WaitForTerminalAsync(started.BuildId);

        Assert.Null(_tracker.Get(started.BuildId, "user-2"));
        Assert.Empty(_tracker.List("user-2"));
        Assert.Null(_tracker.GetCompletedFile(started.BuildId, "user-2"));
        Assert.False(_tracker.Cancel(started.BuildId, "user-2"));
        Assert.False(_tracker.Discard(started.BuildId, "user-2"));
    }

    private static CondensedBookBuildRequest Request(int bookIndex = 0) => new(
        new[] { "/comics/Series/Series - Chapter 0001.cbz", "/comics/Series/Series - Chapter 0002.cbz" },
        "all",
        null,
        bookIndex);

    private void SetupPlan(int issueCount = 2, Func<Task>? beforeReturn = null)
    {
        var files = Enumerable.Range(1, issueCount)
            .Select(i => $"/comics/Series/Series - Chapter {i:D4}.cbz")
            .ToList();

        _email
            .Setup(e => e.PlanCondensedDeliveryAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (beforeReturn is not null)
                {
                    await beforeReturn();
                }

                return new CondensePlanDto(
                    "all",
                    issueCount,
                    issueCount,
                    25 * 1024 * 1024,
                    new[] { new CondensedBookDto("Series 001-002", files, 1024, false) },
                    new Dictionary<string, string>());
            });
    }

    private void SetupBuild(
        Func<IProgress<EpubConversionProgress>?, CancellationToken, Task<CondensedBookFile>> build)
    {
        _email
            .Setup(e => e.CreateCondensedBookAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<IProgress<EpubConversionProgress>?>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                IEnumerable<string> _,
                string? _,
                int? _,
                int _,
                bool _,
                int? _,
                bool _,
                IProgress<EpubConversionProgress>? progress,
                CancellationToken token) => build(progress, token));
    }

    private string CreateBook(string name)
    {
        var directory = Path.Combine(_workDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".epub");
        File.WriteAllText(path, "epub bytes");
        return path;
    }

    private Task<CondensedBookBuildDto> WaitForTerminalAsync(Guid buildId) =>
        WaitForAsync(buildId, b => CondensedBookBuildStatus.IsTerminal(b.Status));

    private async Task<CondensedBookBuildDto> WaitForAsync(
        Guid buildId,
        Func<CondensedBookBuildDto, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var build = _tracker.Get(buildId, Owner);
            if (build is not null && predicate(build))
            {
                return build;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Build {buildId} never reached the expected state.");
    }

    public void Dispose()
    {
        _tracker.Dispose();

        try
        {
            if (Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }
}
