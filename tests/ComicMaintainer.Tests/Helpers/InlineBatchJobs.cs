using System.Collections.Concurrent;
using ComicMaintainer.Core.Interfaces;
using Moq;

namespace ComicMaintainer.Tests.Helpers;

/// <summary>
/// The <see cref="IBatchJobReport"/> handed to post-loop hooks of jobs run by
/// <see cref="InlineBatchJobs"/>: it keeps every reported problem by subject.
/// </summary>
internal sealed class RecordingBatchJobReport : IBatchJobReport
{
    public ConcurrentDictionary<string, string> Problems { get; } = new(StringComparer.Ordinal);

    public Task ReportProblemAsync(string subject, string message)
    {
        Problems[subject] = message;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Makes a mocked <see cref="IComicProcessorService"/> run custom batch jobs
/// inline: every item operation, then the post-loop hook, before the call
/// returns, so a test sees a job's effects as soon as the code under test
/// returns.
/// </summary>
internal static class InlineBatchJobs
{
    /// <summary>
    /// Sets up both <c>RunCustomBatchJobAsync</c> overloads and returns the
    /// report that post-loop hooks taking one write to.
    /// </summary>
    public static RecordingBatchJobReport RunCustomBatchJobsInline(this Mock<IComicProcessorService> processor)
    {
        var report = new RecordingBatchJobReport();

        processor
            .Setup(p => p.RunCustomBatchJobAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<Func<string, CancellationToken, Task<bool>>>(),
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, Task>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string _, IEnumerable<string> items, Func<string, CancellationToken, Task<bool>> operation, string _, Func<CancellationToken, Task>? postLoop, CancellationToken token) =>
            {
                foreach (var item in items)
                {
                    await operation(item, token);
                }
                if (postLoop is not null)
                {
                    await postLoop(token);
                }
                return Guid.NewGuid();
            });

        processor
            .Setup(p => p.RunCustomBatchJobAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<Func<string, CancellationToken, Task<bool>>>(),
                It.IsAny<string>(),
                It.IsAny<Func<IBatchJobReport, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string _, IEnumerable<string> items, Func<string, CancellationToken, Task<bool>> operation, string _, Func<IBatchJobReport, CancellationToken, Task> postLoop, CancellationToken token) =>
            {
                foreach (var item in items)
                {
                    await operation(item, token);
                }
                await postLoop(report, token);
                return Guid.NewGuid();
            });

        return report;
    }
}
