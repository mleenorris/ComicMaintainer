using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Moq;

namespace ComicMaintainer.Tests.Helpers;

/// <summary>
/// Setups for a mocked <see cref="IBatchJobRegistry"/>.
/// </summary>
internal static class BatchJobRegistryMockExtensions
{
    /// <summary>
    /// Makes waiting for any job behave like the real registry when no job is known: a
    /// cancelled wait throws, otherwise the wait reports that there is no such job.
    /// </summary>
    public static void SetupWaitForAnyJobHonoursCancellation(this Mock<IBatchJobRegistry> registry) =>
        registry
            .Setup(r => r.WaitForCompletionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, CancellationToken token) => Finished(null, token));

    /// <summary>
    /// Makes waiting for <paramref name="jobId"/> finish with <paramref name="job"/> like the
    /// real registry would, unless the wait is cancelled.
    /// </summary>
    public static void SetupWait(this Mock<IBatchJobRegistry> registry, Guid jobId, ProcessingJob? job) =>
        registry
            .Setup(r => r.WaitForCompletionAsync(jobId, It.IsAny<CancellationToken>()))
            .Returns((Guid _, CancellationToken token) => Finished(job, token));

    private static Task<ProcessingJob?> Finished(ProcessingJob? job, CancellationToken token) =>
        token.IsCancellationRequested ? Task.FromCanceled<ProcessingJob?>(token) : Task.FromResult(job);
}
