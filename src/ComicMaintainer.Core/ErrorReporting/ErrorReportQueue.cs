using System.Threading.Channels;

namespace ComicMaintainer.Core.ErrorReporting;

/// <summary>
/// Hand-off point between the logging pipeline and the background reporter.
/// </summary>
public interface IErrorReportQueue
{
    /// <summary>
    /// Offers a report for reporting. Returns false when the queue is full.
    /// Never blocks and never throws: it is called from inside a logging call,
    /// where an exception would be swallowed at best and would break the
    /// failing operation's own error handling at worst.
    /// </summary>
    bool TryEnqueue(ErrorReport report);

    /// <summary>Reports dropped because the queue was full, since process start.</summary>
    long DroppedCount { get; }

    /// <summary>Asynchronously reads queued reports until the token is cancelled.</summary>
    IAsyncEnumerable<ErrorReport> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Bounded in-memory queue of pending error reports.
/// </summary>
/// <remarks>
/// The bound matters: an error storm (a failing disk producing one error per
/// file) must not turn into unbounded memory growth on a machine that is
/// already unhealthy. Once full, the newest reports are dropped and counted —
/// the first occurrences already in the queue are the ones worth reporting, and
/// everything is still in the log file regardless.
/// </remarks>
public sealed class ErrorReportQueue : IErrorReportQueue
{
    /// <summary>Default capacity; roughly a few hundred kilobytes of reports.</summary>
    public const int DefaultCapacity = 256;

    private readonly Channel<ErrorReport> _channel;
    private long _dropped;

    public ErrorReportQueue(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            capacity = DefaultCapacity;
        }

        _channel = Channel.CreateBounded<ErrorReport>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            // Wait (rather than DropWrite) so that TryWrite reports the drop
            // instead of silently discarding: the count is the only signal an
            // operator has that reports went missing during a storm.
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public bool TryEnqueue(ErrorReport report)
    {
        if (report is null)
        {
            return false;
        }

        if (_channel.Writer.TryWrite(report))
        {
            return true;
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }

    public IAsyncEnumerable<ErrorReport> ReadAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
