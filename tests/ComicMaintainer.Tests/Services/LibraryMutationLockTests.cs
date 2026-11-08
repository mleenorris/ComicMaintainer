using ComicMaintainer.Core.Services;

namespace ComicMaintainer.Tests.Services;

public sealed class LibraryMutationLockTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly LibraryMutationLock _lock = new();

    public void Dispose() => _lock.Dispose();

    [Fact]
    public async Task Execute_WaitsWhileAnAsynchronousHolderHasTheLock()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asyncHolder = _lock.ExecuteAsync(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return true;
        });
        await entered.Task.WaitAsync(Timeout);

        var syncRan = false;
        var syncCaller = Task.Run(() => _lock.Execute(() => syncRan = true));

        // The synchronous caller (the Suwayomi importer's side) cannot get in
        // while the asynchronous holder (folder combine's side) is inside.
        await Assert.ThrowsAsync<TimeoutException>(() => syncCaller.WaitAsync(TimeSpan.FromMilliseconds(200)));
        Assert.False(syncRan);

        release.TrySetResult();
        await asyncHolder.WaitAsync(Timeout);
        await syncCaller.WaitAsync(Timeout);
        Assert.True(syncRan);
    }

    [Fact]
    public async Task ExecuteAsync_WaitsWhileASynchronousHolderHasTheLock()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var syncHolder = Task.Run(() => _lock.Execute(() =>
        {
            entered.Set();
            release.Wait(Timeout);
        }));
        Assert.True(entered.Wait(Timeout));

        var asyncCaller = _lock.ExecuteAsync(_ => Task.FromResult(42));

        Assert.False(asyncCaller.IsCompleted);
        release.Set();
        await syncHolder.WaitAsync(Timeout);
        Assert.Equal(42, await asyncCaller.WaitAsync(Timeout));
    }

    [Fact]
    public async Task ExecuteAsync_CancelledWhileWaiting_ThrowsWithoutRunningTheAction()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var holder = Task.Run(() => _lock.Execute(() =>
        {
            entered.Set();
            release.Wait(Timeout);
        }));
        Assert.True(entered.Wait(Timeout));
        using var cts = new CancellationTokenSource();
        var ran = false;

        var waiting = _lock.ExecuteAsync(_ =>
        {
            ran = true;
            return Task.FromResult(true);
        }, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(ran);
        release.Set();
        await holder.WaitAsync(Timeout);
    }

    [Fact]
    public void Execute_ReleasesTheLockWhenTheActionThrows()
    {
        Assert.Throws<IOException>(() => _lock.Execute(() => throw new IOException("disk")));

        Assert.Equal(7, _lock.Execute(() => 7));
    }

    [Fact]
    public async Task Execute_ReentryFromTheHolder_ThrowsInsteadOfDeadlocking()
    {
        var syncReentry = Assert.Throws<InvalidOperationException>(() =>
            _lock.Execute(() => _lock.Execute(() => 1)));
        Assert.Contains("not reentrant", syncReentry.Message);

        var asyncReentry = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _lock.ExecuteAsync(_ => _lock.ExecuteAsync(_ => Task.FromResult(1))));
        Assert.Contains("not reentrant", asyncReentry.Message);

        // Both attempts left the lock free.
        Assert.Equal(2, _lock.Execute(() => 2));
    }

    [Fact]
    public async Task ExecuteAsync_WorkStartedInsideTheSectionCanTakeTheLockOnceTheSectionEnded()
    {
        Task<int>? later = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await _lock.ExecuteAsync(_ =>
        {
            // Started inside the section, so it inherits the section's flow,
            // but it only asks for the lock after the section has ended.
            later = Task.Run(async () =>
            {
                await release.Task;
                return await _lock.ExecuteAsync(_ => Task.FromResult(5));
            });
            return Task.FromResult(true);
        });
        release.TrySetResult();

        Assert.Equal(5, await later!.WaitAsync(Timeout));
    }
}
