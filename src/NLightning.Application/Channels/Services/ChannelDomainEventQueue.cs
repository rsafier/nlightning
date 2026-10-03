namespace NLightning.Application.Channels.Services;

using Domain.Channels.Commitments.Events;

/// <summary>
/// Collects the domain events of the transitions persisted while handling one channel message (scoped: one per message
/// scope). <c>ChannelManager</c> drains it after it released the channel's lock and hands the events to the
/// <c>IHtlcSwitch</c>, which must run outside the lock (it may call <c>IChannelOperations</c>, which take it).
/// </summary>
public sealed class ChannelDomainEventQueue
{
    private readonly List<IChannelDomainEvent> _events = [];

    /// <summary>Adds the events of a persisted transition, in order.</summary>
    public void Enqueue(IEnumerable<IChannelDomainEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events.AddRange(events);
    }

    /// <summary>Returns every queued event in order and empties the queue.</summary>
    public IReadOnlyList<IChannelDomainEvent> Drain()
    {
        var drained = _events.ToList();
        _events.Clear();
        return drained;
    }
}