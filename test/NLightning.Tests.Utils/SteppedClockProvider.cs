using System.Diagnostics.CodeAnalysis;

namespace NLightning.Tests.Utils;

/// <summary>
/// The system clock plus an offset that only <see cref="Advance"/> moves; its timers fire (on the caller's thread)
/// only from <see cref="Advance"/>, once the clock reaches their due time. Starting from the real time keeps
/// timestamped payloads (BOLT 11 invoices, gossip timestamps) unexpired while every deadline in the test becomes
/// deterministic: nothing fires unless the test advances the clock, and CPU load cannot stretch or reorder timers.
/// </summary>
/// <remarks>
/// Shared de-flake utility (batch: the loaded-run flake pass): freeze the clock, assert the intermediate state,
/// <see cref="Advance"/> past the deadline, assert the outcome. Timers created through this provider never run on
/// thread-pool timing threads.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class SteppedClockProvider : TimeProvider
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

    /// <summary>Moves the clock forward by <paramref name="by"/> and fires every timer that became due, in due order,
    /// on the caller's thread.</summary>
    public void Advance(TimeSpan by)
    {
        List<SteppedTimer> due;
        lock (_gate)
        {
            _offset += by;
            var now = System.GetUtcNow() + _offset;
            due = _timers.Where(t => t.DueAt is { } at && at <= now).OrderBy(t => t.DueAt).ToList();
            foreach (var timer in due)
                timer.DueAt = null;
        }

        foreach (var timer in due)
            timer.Fire();
    }

    private sealed class SteppedTimer(SteppedClockProvider owner, TimerCallback callback, object? state) : ITimer
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