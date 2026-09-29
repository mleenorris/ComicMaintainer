using ComicMaintainer.Core.ErrorReporting.Interfaces;

namespace ComicMaintainer.Core.ErrorReporting.Services;

/// <summary>
/// Fixed-capacity, thread-safe ring buffer of recent log lines.
/// </summary>
/// <remarks>
/// Capacity is bounded at construction so the buffer cannot grow with log
/// volume; a busy watcher run would otherwise retain an unbounded slice of the
/// library's file names in memory.
/// </remarks>
public sealed class ErrorReportLogBuffer : IErrorReportLogBuffer
{
    /// <summary>Default number of lines retained.</summary>
    public const int DefaultCapacity = 200;

    /// <summary>Maximum characters kept per line.</summary>
    private const int MaxLineLength = 2000;

    private readonly int _capacity;
    private readonly Queue<string> _lines;
    private readonly Lock _gate = new();

    public ErrorReportLogBuffer(int capacity = DefaultCapacity)
    {
        _capacity = Math.Clamp(capacity, 10, 1000);
        _lines = new Queue<string>(_capacity);
    }

    /// <inheritdoc />
    public void Add(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        var trimmed = line.Length > MaxLineLength ? line[..MaxLineLength] : line;

        lock (_gate)
        {
            if (_lines.Count >= _capacity)
            {
                _lines.Dequeue();
            }
            _lines.Enqueue(trimmed);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Snapshot(int maxLines)
    {
        if (maxLines <= 0)
        {
            return Array.Empty<string>();
        }

        lock (_gate)
        {
            var take = Math.Min(maxLines, _lines.Count);
            return take == 0
                ? Array.Empty<string>()
                : _lines.Skip(_lines.Count - take).ToList();
        }
    }
}
