using Microsoft.Extensions.Options;

namespace ComicMaintainer.Tests.Helpers;

/// <summary>
/// Minimal in-memory implementation of <see cref="IOptionsMonitor{TOptions}"/> for use in tests.
/// Allows replacing the current value via <see cref="Set"/>, which fires registered change
/// callbacks — useful for verifying hot-reload behaviour without depending on the configuration
/// system or file watchers.
/// </summary>
public sealed class TestOptionsMonitor<T> : IOptionsMonitor<T> where T : class
{
    private readonly object _lock = new();
    private readonly List<Action<T, string?>> _listeners = new();
    private T _currentValue;

    public TestOptionsMonitor(T initialValue)
    {
        _currentValue = initialValue;
    }

    public T CurrentValue
    {
        get
        {
            lock (_lock)
            {
                return _currentValue;
            }
        }
    }

    public T Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener)
    {
        lock (_lock)
        {
            _listeners.Add(listener);
        }
        return new Subscription(() =>
        {
            lock (_lock)
            {
                _listeners.Remove(listener);
            }
        });
    }

    /// <summary>
    /// Replaces the current value and synchronously notifies all registered listeners.
    /// </summary>
    public void Set(T value)
    {
        Action<T, string?>[] snapshot;
        lock (_lock)
        {
            _currentValue = value;
            snapshot = _listeners.ToArray();
        }
        foreach (var listener in snapshot)
        {
            listener(value, null);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private Action? _dispose;
        public Subscription(Action dispose) => _dispose = dispose;
        public void Dispose()
        {
            var d = Interlocked.Exchange(ref _dispose, null);
            d?.Invoke();
        }
    }
}
