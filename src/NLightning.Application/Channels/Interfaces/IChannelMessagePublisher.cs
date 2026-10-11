namespace NLightning.Application.Channels.Interfaces;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;

/// <summary>
/// Puts channel messages that are not replies to a peer message on a peer's outbox (the send side of BOLT2 plan N6-T2:
/// updates from <c>IChannelOperations</c> and the <c>commitment_signed</c> of the commit scheduler).
/// </summary>
/// <remarks>
/// Implemented by <c>ChannelManager</c>, which raises the messages through
/// <c>IChannelManager.OnResponseMessageReady</c>, so they take the same path as replies (<c>PeerManager</c> enqueues
/// them on the peer's <c>PeerOutbox</c>; a peer that is not connected drops them, and channel_reestablish retransmits
/// what the peer missed). Call it while holding the channel's lock, right after the transition that produced the
/// messages was persisted, so wire order equals persist order. It never blocks and never throws for a missing peer.
/// </remarks>
public interface IChannelMessagePublisher
{
    /// <summary>Enqueues <paramref name="messages"/> for <paramref name="peerPubKey"/>, in order.</summary>
    void Publish(CompactPubKey peerPubKey, IReadOnlyList<IChannelMessage> messages);
}