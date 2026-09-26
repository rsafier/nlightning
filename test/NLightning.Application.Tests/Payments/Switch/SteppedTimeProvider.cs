using System.Diagnostics.CodeAnalysis;

namespace NLightning.Application.Tests.Payments.Switch;

/// <summary>
/// The system clock plus an offset that only <see cref="Advance"/> moves; its one-shot timers fire (on the caller's
/// thread) only from <see cref="Advance"/>, once the clock reaches their due time. Starting from the real time keeps
/// invoices (stamped with the real BOLT 11 timestamp) unexpired.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class SteppedTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<SteppedTimer> _timers = [];
    private TimeSpan _offset = TimeSpan.Zero;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return System.GetUtcNow() + _offset;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new SteppedTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>The timers still waiting (not fired, not disposed).</summary>
    public int PendingTimers
    {
        get
        {
            lock (_gate)
                return _timers.Count(t => t.DueAt is not null);
        }
    }

    public void Advance(TimeSpan by)
    {
        List<SteppedTimer> due;
        lock (_gate)
        {
            _offset += by;
            var now = System.GetUtcNow() + _offset;
            due = _timers.Where(t => t.DueAt is { } at && at <= now).ToList();
            foreach (var timer in due)
                timer.DueAt = null;
        }

        foreach (var timer in due)
            timer.Fire();
    }

    private sealed class SteppedTimer(SteppedTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : System.GetUtcNow() + owner._offset + dueTime;
                if (!owner._timers.Contains(this))
                    owner._timers.Add(this);
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                DueAt = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}