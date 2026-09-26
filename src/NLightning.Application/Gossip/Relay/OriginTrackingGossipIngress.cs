namespace NLightning.Application.Gossip.Relay;

using Domain.Gossip.Interfaces;
using Domain.Node.Interfaces;
using Domain.Protocol.Interfaces;

/// <summary>
/// The peer services' <see cref="IGossipIngress"/> (BOLT 7 plan G3-T3): records the origin of every 256/257/258 a
/// peer sends in the <see cref="GossipOriginTracker"/>, then hands it to the graph's ingress unchanged, so the relay
/// can leave out the peers a message came from.
/// </summary>
public sealed class OriginTrackingGossipIngress : IGossipIngress
{
    private readonly IGossipIngress _inner;
    private readonly GossipOriginTracker _tracker;

    public OriginTrackingGossipIngress(IGossipIngress inner, GossipOriginTracker tracker)
    {
        _inner = inner;
        _tracker = tracker;
    }

    /// <inheritdoc />
    public bool IsEnabled => _inner.IsEnabled;

    /// <inheritdoc />
    public bool TryEnqueue(IPeerService origin, IMessage message)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(message);

        if (_inner.IsEnabled)
            _tracker.Record(message, origin.PeerPubKey);

        return _inner.TryEnqueue(origin, message);
    }
}