using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// The per-file lock that keeps two operations from rewriting or renaming one archive at once.
/// </summary>
public sealed class FileOperationLockTests : IDisposable
{
    // Long enough for an unblocked acquisition to complete on a busy machine; a correct lock
    // never completes the blocked acquisition however long the test waits.
    private static readonly TimeSpan BlockedWait = TimeSpan.FromMilliseconds(300);

    private readonly string _directory = Path.Join(Path.GetTempPath(), $"file_lock_tests_{Guid.NewGuid():N}");

    public FileOperationLockTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose() => TestIO.TryDeleteDirectory(_directory);

    [Fact]
    public async Task AcquireAsync_SameFile_WaitsUntilTheHolderReleases()
    {
        var fileLock = new FileOperationLock();
        var path = Path.Join(_directory, "Batman 001.cbz");

        var first = await fileLock.AcquireAsync(path);
        var second = fileLock.AcquireAsync(path).AsTask();

        await Task.Delay(BlockedWait);
        Assert.False(second.IsCompleted);

        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
    }

    [Fact]
    public async Task AcquireAsync_PathSpelledInAnotherCase_SharesTheLock()
    {
        // On a case-insensitive volume both spellings open the same archive.
        var fileLock = new FileOperationLock();
        var path = Path.Join(_directory, "Tower Of God - Chapter 0001.cbz");

        using var held = await fileLock.AcquireAsync(path);
        var other = fileLock.AcquireAsync(Path.Join(_directory, "TOWER OF GOD - CHAPTER 0001.CBZ")).AsTask();

        await Task.Delay(BlockedWait);
        Assert.False(other.IsCompleted);

        held.Dispose();
        (await other.WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
    }

    [Fact]
    public async Task AcquireAsync_SameFileThroughDotSegments_SharesTheLock()
    {
        var fileLock = new FileOperationLock();
        Directory.CreateDirectory(Path.Join(_directory, "Series"));
        var path = Path.Join(_directory, "Series", "Issue 1.cbz");

        using var held = await fileLock.AcquireAsync(path);
        var other = fileLock.AcquireAsync(Path.Join(_directory, "Series", "..", "Series", ".", "Issue 1.cbz")).AsTask();

        await Task.Delay(BlockedWait);
        Assert.False(other.IsCompleted);

        held.Dispose();
        (await other.WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
    }

    [Fact]
    public async Task AcquireAsync_DifferentFiles_DoNotBlockEachOther()
    {
        var fileLock = new FileOperationLock();

        using var first = await fileLock.AcquireAsync(Path.Join(_directory, "Batman 001.cbz"));
        var second = fileLock.AcquireAsync(Path.Join(_directory, "Batman 002.cbz")).AsTask();

        (await second.WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
    }

    [Fact]
    public async Task AcquireAsync_CancelledWhileWaiting_ThrowsAndLeavesNoEntryBehind()
    {
        var fileLock = new FileOperationLock();
        var path = Path.Join(_directory, "Saga 001.cbz");
        var held = await fileLock.AcquireAsync(path);
        using var cts = new CancellationTokenSource();

        var waiting = fileLock.AcquireAsync(path, cts.Token).AsTask();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, fileLock.ActiveKeyCount);

        held.Dispose();
        Assert.Equal(0, fileLock.ActiveKeyCount);

        // The cancelled waiter did not take the lock, so it is free again.
        (await fileLock.AcquireAsync(path).AsTask().WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
    }

    [Fact]
    public async Task Release_LastHolder_RemovesTheEntry()
    {
        var fileLock = new FileOperationLock();

        for (var i = 0; i < 50; i++)
        {
            (await fileLock.AcquireAsync(Path.Join(_directory, $"Issue {i}.cbz"))).Dispose();
        }

        Assert.Equal(0, fileLock.ActiveKeyCount);
    }

    [Fact]
    public async Task Dispose_CalledTwice_ReleasesOnlyOnce()
    {
        var fileLock = new FileOperationLock();
        var path = Path.Join(_directory, "Issue 1.cbz");

        var first = await fileLock.AcquireAsync(path);
        first.Dispose();
        var second = await fileLock.AcquireAsync(path);

        // A second Dispose of the first lease must not release the lock the second lease holds.
        first.Dispose();
        var third = fileLock.AcquireAsync(path).AsTask();
        await Task.Delay(BlockedWait);
        Assert.False(third.IsCompleted);

        second.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
    }

    [Fact]
    public async Task AcquireAsync_ManyConcurrentOperationsOnOneFile_NeverOverlap()
    {
        var fileLock = new FileOperationLock();
        var path = Path.Join(_directory, "Contended.cbz");
        var inside = 0;
        var maxInside = 0;

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            using (await fileLock.AcquireAsync(path))
            {
                var now = Interlocked.Increment(ref inside);
                InterlockedMax(ref maxInside, now);
                await Task.Delay(5);
                Interlocked.Decrement(ref inside);
            }
        })));

        Assert.Equal(1, maxInside);
        Assert.Equal(0, fileLock.ActiveKeyCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AcquireAsync_BlankPath_Throws(string path)
    {
        var fileLock = new FileOperationLock();

        await Assert.ThrowsAsync<ArgumentException>(() => fileLock.AcquireAsync(path).AsTask());
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
