using Microsoft.Extensions.Time.Testing;

namespace ComicMaintainer.Tests.Helpers;

/// <summary>
/// <see cref="FakeTimeProvider"/> wrapper that counts the timers created
/// through it, so a test can wait for a background loop to start its next
/// delay before advancing the clock past it.
/// </summary>
public sealed class TimerTrackingTimeProvider : TimeProvider
{
    private readonly FakeTimeProvider _time;
    private int _timersCreated;

    public TimerTrackingTimeProvider(DateTimeOffset start)
    {
        _time = new FakeTimeProvider(start);
    }

    /// <summary>Timers created so far, including ones that have already fired.</summary>
    public int TimersCreated => Volatile.Read(ref _timersCreated);

    public override long TimestampFrequency => _time.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => _time.LocalTimeZone;

    public override DateTimeOffset GetUtcNow() => _time.GetUtcNow();

    public override long GetTimestamp() => _time.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = _time.CreateTimer(callback, state, dueTime, period);
        Interlocked.Increment(ref _timersCreated);
        return timer;
    }

    public void Advance(TimeSpan delta) => _time.Advance(delta);

    /// <summary>Waits until at least <paramref name="count"/> timers have been created.</summary>
    public async Task WaitForTimersAsync(int count, TimeSpan? patience = null)
    {
        var deadline = DateTime.UtcNow + (patience ?? TimeSpan.FromSeconds(10));
        while (TimersCreated < count)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected {count} timer(s) but only {TimersCreated} were created.");
            }

            await Task.Delay(10);
        }
    }
}
