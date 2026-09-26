namespace NLightning.Application.Gossip.Interfaces;

using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Events;

/// <summary>
/// Our <c>channel_update</c>s (BOLT 7): the direct exchange with the channel peer (ABCD W1-E, NL-099 subset) and,
/// for announced channels, the public update (BOLT 7 plan G1-T5).
/// </summary>
/// <remarks>
/// For an unannounced channel our routing policy (fee, CLTV delta, HTLC limits from <c>NodeOptions.Routing</c>) only
/// reaches the peer this way: once a channel is <c>Open</c> we sign an update with the node key (<c>dont_forward</c>
/// set) and hand it to the peer (after the <c>channel_ready</c> it follows). LND then shows it as our policy
/// (<c>GetChanInfo</c>) and can build private route hints through the channel. Once the channel is announced the update
/// is signed again with <c>dont_forward</c> clear and the real short channel id, and also goes to the graph and the
/// relay. The peer's own update is checked and kept in memory, for route hints and UPDATE-class failures.
/// </remarks>
public interface IChannelUpdateService
{
    /// <summary>
    /// Raised, while the channel's lock is held, when an update of ours must go to the channel peer. The handler
    /// must only enqueue it (it goes after whatever that channel queued before, e.g. its <c>channel_ready</c>).
    /// </summary>
    event EventHandler<ChannelUpdateReadyEventArgs>? OnChannelUpdateReady;

    /// <summary>
    /// Builds and signs our current <c>channel_update</c> for <paramref name="channel"/> (<c>dont_forward</c> set
    /// unless the channel is announced; an announced channel's update is also handed to the graph and the relay). Its
    /// timestamp is newer than any update made before for that channel.
    /// </summary>
    /// <param name="channel">An open channel with a short channel id.</param>
    /// <param name="disabled">Set the <c>disable</c> bit (e.g. before closing).</param>
    /// <exception cref="InvalidOperationException">The channel has no short channel id or funding output yet.</exception>
    ChannelUpdateMessage CreateChannelUpdate(ChannelModel channel, bool disabled = false);

    /// <summary>
    /// The channel was just announced (its <c>channel_announcement</c> assembled): signs our public update
    /// (<c>dont_forward</c> clear, real short channel id, a newer timestamp), raises it through
    /// <see cref="OnChannelUpdateReady"/> for the peer and hands it to the graph and the relay. Call it under the
    /// channel's lock. Null when the channel is not Open or has no valid policy.
    /// </summary>
    ChannelUpdateMessage? OnChannelAnnounced(ChannelModel channel);

    /// <summary>
    /// Builds our update for the channel and raises <see cref="OnChannelUpdateReady"/> under the channel's lock, if
    /// the channel is <c>Open</c>. Call it without holding any channel lock (e.g. after <c>channel_reestablish</c>).
    /// </summary>
    Task SendChannelUpdateAsync(ChannelId channelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends our update for every <c>Open</c> channel with the peer, e.g. once a new connection to it finished the
    /// init exchange: the last one again when its policy is still current, a new one otherwise. Each is raised
    /// through <see cref="OnChannelUpdateReady"/> under its channel's lock. Call it without holding any channel lock.
    /// </summary>
    Task SendChannelUpdatesToPeerAsync(CompactPubKey peerPubKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks a <c>channel_update</c> the peer sent (our chain, a channel we have with that peer, the peer as
    /// origin, its node signature, a newer timestamp) and keeps it. Anything else is ignored.
    /// </summary>
    /// <returns>Whether the update was kept.</returns>
    bool HandleRemoteChannelUpdate(CompactPubKey peerPubKey, ChannelUpdateMessage message);

    /// <summary>
    /// The latest valid <c>channel_update</c> the peer sent for the channel (its policy for HTLCs towards us).
    /// </summary>
    bool TryGetRemoteChannelUpdate(ChannelId channelId, out ChannelUpdatePayload? update);

    /// <summary>
    /// The latest <c>channel_update</c> we made for the channel (e.g. for a BOLT 4 UPDATE-class failure).
    /// </summary>
    bool TryGetLocalChannelUpdate(ChannelId channelId, out ChannelUpdateMessage? update);
}