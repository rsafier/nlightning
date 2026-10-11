namespace NLightning.Domain.Channels.Interfaces;

using Crypto.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Events;
using Models;
using Node.Options;
using ValueObjects;

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
    /// Processes a <c>start_batch</c> group of <c>commitment_signed</c> messages (BOLT 2 "Batching channel messages";
    /// splicing plan D15, SP-OP-03..07) from <paramref name="peerPubKey"/> under one acquisition of the channel's lock,
    /// like <see cref="HandleChannelMessageAsync"/>: one CS per active funding, answered by a single
    /// <c>revoke_and_ack</c>. The per-peer inbound loop (<c>PeerManager</c>, lane SP1-A) builds the batch; the
    /// processing is lanes SP1-B/SP1-D's (the default throws until then).
    /// </summary>
    Task HandleCommitmentSignedBatchAsync(CommitmentSignedBatch batch, FeatureOptions negotiatedFeatures,
                                          CompactPubKey peerPubKey) =>
        throw new NotImplementedException("Lanes SP1-A/SP1-B (SP1-A-T3, SP1-B-T2)");

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

    /// <summary>
    /// The channels of <paramref name="peerPubKey"/> that should carry updates (ReadyForThem, Open, ShuttingDown,
    /// Negotiating) whose <c>channel_reestablish</c> we sent on the peer's current connection and that are still not
    /// reestablished on it: the peer has not answered (NL-796; the peer manager's reestablish deadline).
    /// A channel waiting for its funding (V1FundingSigned), one where only we sent channel_ready (ReadyForUs: the
    /// funding may still be pending on the peer's side, and LND sends no reestablish for it until it sees the
    /// confirmation, NL-891) or whose close is agreed (Closing) is never listed: a peer may have forgotten it, not
    /// confirmed it yet or stopped its link, and nothing waits on its reestablish.
    /// </summary>
    IReadOnlyList<ChannelId> GetChannelsAwaitingPeerReestablish(CompactPubKey peerPubKey) => [];

    /// <summary>
    /// Registers a channel loaded from the database at startup (memory and signer), after resuming its state (BOLT2
    /// plan N7-T5): Closed and Stale channels are skipped; a funder's channel stopped between persisting
    /// funding_signed and moving to V1FundingSigned moves on when its funding transaction was watched (so it may have
    /// been published) and is forgotten (persisted Stale) otherwise, since BOLT 2 says a funder that has not broadcast
    /// SHOULD NOT remember the channel.
    /// </summary>
    Task RegisterExistingChannelAsync(ChannelModel channel);

    /// <summary>
    /// Starts opening a channel we fund: under the temporary channel's lock, stores <paramref name="channel"/> as a
    /// temporary channel of <paramref name="peerPubKey"/> and raises <paramref name="openChannelMessage"/> through
    /// <see cref="OnResponseMessageReady"/>, so it goes out through the peer's ordered send path like every other
    /// channel message (BOLT2 plan §3.7).
    /// </summary>
    Task StartOpeningChannelAsync(CompactPubKey peerPubKey, ChannelModel channel, IChannelMessage openChannelMessage);
}