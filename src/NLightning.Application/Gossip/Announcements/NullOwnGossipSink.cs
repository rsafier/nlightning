namespace NLightning.Application.Gossip.Announcements;

using Domain.Gossip.Interfaces;
using Domain.Money;
using Domain.Protocol.Payloads;

/// <summary>
/// The default <see cref="IOwnGossipSink"/> until the graph store is registered (BOLT 7 plan G2-T4): ignores our own
/// gossip.
/// </summary>
public sealed class NullOwnGossipSink : IOwnGossipSink
{
    /// <inheritdoc />
    public void AddOwnChannelAnnouncement(ChannelAnnouncementPayload announcement, LightningMoney capacity)
    {
    }

    /// <inheritdoc />
    public void AddOwnChannelUpdate(ChannelUpdatePayload update)
    {
    }

    /// <inheritdoc />
    public void AddOwnNodeAnnouncement(NodeAnnouncementPayload announcement)
    {
    }
}