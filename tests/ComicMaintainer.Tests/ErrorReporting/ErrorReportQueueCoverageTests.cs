using ComicMaintainer.Core.ErrorReporting;

namespace ComicMaintainer.Tests.ErrorReporting;

public class ErrorReportQueueCoverageTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_FallsBackToTheDefaultCapacityForANonPositiveValue(int capacity)
    {
        var queue = new ErrorReportQueue(capacity);

        for (var i = 0; i < ErrorReportQueue.DefaultCapacity; i++)
        {
            Assert.True(queue.TryEnqueue(CreateReport(i)));
        }

        Assert.False(queue.TryEnqueue(CreateReport(ErrorReportQueue.DefaultCapacity)));
        Assert.Equal(1, queue.DroppedCount);
    }

    [Fact]
    public void TryEnqueue_RejectsANullReportWithoutCountingADrop()
    {
        var queue = new ErrorReportQueue(1);

        Assert.False(queue.TryEnqueue(null!));
        Assert.Equal(0, queue.DroppedCount);
        Assert.True(queue.TryEnqueue(CreateReport(1)));
    }

    private static ErrorReport CreateReport(int index) => new()
    {
        Fingerprint = $"fingerprint-{index}",
        Level = "Error",
        MessageTemplate = "Failure {Index}",
        RenderedMessage = $"Failure {index}",
        TimestampUtc = DateTime.UnixEpoch
    };
}
