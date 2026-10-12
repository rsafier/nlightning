namespace NLightning.Application.Gossip.Announcements;

using Domain.Gossip.Interfaces;
using Domain.Money;
using Domain.Protocol.Payloads;
using Relay.Interfaces;

/// <summary>
/// Hands the gossip we generate ourselves (BOLT 7 plan §3.2 step 5) to its two consumers: the graph
/// (<see cref="IOwnGossipSink"/>, G2-T4) and the relay to our peers (<see cref="IGossipRelayScheduler"/>, G1-T7).
/// </summary>
/// <remarks>Only records and enqueues: callers may hold a channel's lock. Every call is idempotent.</remarks>
public sealed class OwnGossipPublisher
{
    private readonly IOwnGossipSink _sink;
    private readonly IGossipRelayScheduler _relay;

    public OwnGossipPublisher(IOwnGossipSink sink, IGossipRelayScheduler relay)
    {
        _sink = sink;
        _relay = relay;
    }

    /// <summary>Our assembled <c>channel_announcement</c> (four verified signatures) and the channel's capacity.</summary>
    public void PublishChannelAnnouncement(ChannelAnnouncementPayload announcement, LightningMoney capacity)
    {
        _sink.AddOwnChannelAnnouncement(announcement, capacity);
        _relay.EnqueueOwnChannelAnnouncement(announcement);
    }

    /// <summary>Our signed <c>channel_update</c> of an announced channel.</summary>
    public void PublishChannelUpdate(ChannelUpdatePayload update)
    {
        _sink.AddOwnChannelUpdate(update);
        _relay.EnqueueOwnChannelUpdate(update);
    }

    /// <summary>Our signed <c>node_announcement</c>.</summary>
    public void PublishNodeAnnouncement(NodeAnnouncementPayload announcement)
    {
        _sink.AddOwnNodeAnnouncement(announcement);
        _relay.EnqueueOwnNodeAnnouncement(announcement);
    }

    /// <summary>Our own <c>channel_announcement_2</c> (NL-878) into the graph and out to the v2 peers.</summary>
    public void PublishChannelAnnouncement2(ChannelAnnouncement2Payload announcement, LightningMoney capacity)
    {
        _sink.AddOwnChannelAnnouncement2(announcement, capacity);
        _relay.EnqueueOwnChannelAnnouncement2(announcement);
    }

    /// <summary>Our own <c>channel_update_2</c> into the graph and out to the v2 peers.</summary>
    public void PublishChannelUpdate2(ChannelUpdate2Payload update)
    {
        _sink.AddOwnChannelUpdate2(update);
        _relay.EnqueueOwnChannelUpdate2(update);
    }

    /// <summary>Our own <c>node_announcement_2</c> into the graph and out to the v2 peers.</summary>
    public void PublishNodeAnnouncement2(NodeAnnouncement2Payload announcement)
    {
        _sink.AddOwnNodeAnnouncement2(announcement);
        _relay.EnqueueOwnNodeAnnouncement2(announcement);
    }
}