using System.Diagnostics.CodeAnalysis;

namespace NLightning.Application.Tests.Gossip.Relay;

/// <summary>
/// A clock for the relay tests that moves only when told to; its timers never fire (the tests drive the relay ticks
/// themselves, so no background tick races them).
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class RelayTestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    public int TimersCreated { get; private set; }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        TimersCreated++;
        return new SilentTimer();
    }

    private sealed class SilentTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}