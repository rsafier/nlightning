namespace NLightning.Application.Gossip.Relay;

using Domain.Gossip.Interfaces;
using Domain.Node.Interfaces;
using Domain.Protocol.Interfaces;

/// <summary>
/// The peer services' <see cref="IGossipIngress"/> (BOLT 7 plan G3-T3): records the origin of every 256/257/258 a
/// peer sends in the <see cref="GossipOriginTracker"/>, then hands it to the graph's ingress unchanged, so the relay
/// can leave out the peers a message came from. Nothing is recorded while the relay of others' gossip is off: the
/// tracker's up to <see cref="GossipOriginTracker.DefaultCapacity"/> entries (about 15 MB on mainnet, NL-384) would
/// serve nobody.
/// </summary>
public sealed class OriginTrackingGossipIngress : IGossipIngress
{
    private readonly IGossipIngress _inner;
    private readonly GossipOriginTracker _tracker;
    private readonly bool _recordOrigins;

    /// <param name="inner">The graph's ingress.</param>
    /// <param name="tracker">Where the origins go.</param>
    /// <param name="recordOrigins">False while the relay of others' gossip is off (nothing is recorded).</param>
    public OriginTrackingGossipIngress(IGossipIngress inner, GossipOriginTracker tracker, bool recordOrigins = true)
    {
        _inner = inner;
        _tracker = tracker;
        _recordOrigins = recordOrigins;
    }

    /// <inheritdoc />
    public bool IsEnabled => _inner.IsEnabled;

    /// <inheritdoc />
    public bool TryEnqueue(IPeerService origin, IMessage message)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(message);

        if (_recordOrigins && _inner.IsEnabled)
            _tracker.Record(message, origin.PeerPubKey);

        return _inner.TryEnqueue(origin, message);
    }
}