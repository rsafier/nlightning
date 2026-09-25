namespace NLightning.Domain.Channels.Interfaces;

using Crypto.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Events;
using Models;
using Node.Options;

public interface IChannelManager
{
    /// <summary>
    /// Raised for every message a channel transition wants to send, including the replies to
    /// <see cref="HandleChannelMessageAsync"/>, in wire order.
    /// </summary>
    /// <remarks>
    /// Raised synchronously while the channel's lock is held, so a subscriber must only enqueue (never block or send
    /// inline): that is what keeps wire order equal to persist order (BOLT2 plan D2).
    /// </remarks>
    event EventHandler<ChannelResponseMessageEventArgs> OnResponseMessageReady;

    /// <summary>
    /// Processes one channel message from <paramref name="peerPubKey"/> under the channel's lock. The replies are
    /// raised through <see cref="OnResponseMessageReady"/> (the only way they are delivered, NL-234), in order, before
    /// the lock is released and before this task completes. Failures are thrown as channel-scoped
    /// <c>ChannelErrorException</c>/<c>ChannelWarningException</c>; a <c>ChannelFailedException</c> means the channel
    /// is (now) failed and its <c>error</c> must be sent, which does not require closing the connection (BOLT 1).
    /// </summary>
    Task HandleChannelMessageAsync(IChannelMessage message, FeatureOptions negotiatedFeatures,
                                   CompactPubKey peerPubKey);

    /// <summary>
    /// A new connection with <paramref name="peerPubKey"/> is ready (after <c>init</c>), and nothing else was sent
    /// on it for any channel (BOLT 2 Message Retransmission, plan N7-T2): reverts the peer's uncommitted updates, then
    /// raises our <c>channel_reestablish</c> for every Open channel with the peer through
    /// <see cref="OnResponseMessageReady"/>.
    /// </summary>
    /// <returns>The stored <c>error</c> of every failed channel with the peer, to re-send (B2-RE-05).</returns>
    Task<IReadOnlyList<ErrorMessage>> OnPeerConnectedAsync(CompactPubKey peerPubKey);

    /// <summary>
    /// The connection the peer's messages were handled on is gone and its last message was handled: reverts the peer's
    /// uncommitted updates on every channel with it (BOLT 2: "upon disconnection").
    /// </summary>
    Task OnPeerDisconnectedAsync(CompactPubKey peerPubKey);

    /// <summary>
    /// The peer's connection changed (a new one replaced it, or it dropped): from now on none of its channels is
    /// reestablished until the peer's <c>channel_reestablish</c> arrives on the new connection. Synchronous, so it can
    /// run before the new connection carries anything.
    /// </summary>
    void OnPeerConnectionChanged(CompactPubKey peerPubKey);

    Task RegisterExistingChannelAsync(ChannelModel channel);

    /// <summary>
    /// Starts opening a channel we fund: under the temporary channel's lock, stores <paramref name="channel"/> as a
    /// temporary channel of <paramref name="peerPubKey"/> and raises <paramref name="openChannelMessage"/> through
    /// <see cref="OnResponseMessageReady"/>, so it goes out through the peer's ordered send path like every other
    /// channel message (BOLT2 plan §3.7).
    /// </summary>
    Task StartOpeningChannelAsync(CompactPubKey peerPubKey, ChannelModel channel, IChannelMessage openChannelMessage);
}