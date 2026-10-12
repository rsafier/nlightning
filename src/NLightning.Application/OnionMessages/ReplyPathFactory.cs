namespace NLightning.Application.OnionMessages;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages.Interfaces;

/// <summary>
/// Builds the <c>reply_path</c> of our own messages (BOLT 4 writer: <c>first_node_id</c> is the unblinded
/// introduction node, <c>first_path_key</c> its path key; plan D7): a path whose introduction node is a connected peer
/// that negotiated <c>option_onion_messages</c> (one we have an open channel with first, and among those the peer the
/// message itself leaves through when the caller names it: the recipient's side reached that peer, so it can route the
/// reply back to it, NL-1155), else a one-hop path to ourselves, ended by dummy hops of our own node (<see cref="OnionMessageOptions.BlindedPathDummyHops"/>, NL-525).
/// Our hop's <c>path_id</c> is the secret the <see cref="PendingReplyRegistry"/> recognizes.
/// </summary>
/// <remarks>
/// With a peer as the introduction node our node id stays hidden inside the path (plan D7: a private node should not
/// reveal it), and CLN only uses introduction nodes that advertise bit 38/39 (plan F-05). The dummy hops (BOLT 4: the
/// recipient "MAY add additional dummy hops at the end of the path (which it will ignore on receipt)") keep the sender
/// from telling our hop from the padding after it; we peel them ourselves when the reply comes back.
/// </remarks>
public sealed class ReplyPathFactory
{
    private readonly IBlindedMessagePathBuilder _pathBuilder;
    private readonly OnionMessagePathFinder _pathFinder;
    private readonly CompactPubKey _ourNodeId;
    private readonly int _dummyHops;

    public ReplyPathFactory(IBlindedMessagePathBuilder pathBuilder, OnionMessagePathFinder pathFinder,
                            CompactPubKey ourNodeId, int dummyHops = OnionMessageOptions.DefaultBlindedPathDummyHops)
    {
        _pathBuilder = pathBuilder;
        _pathFinder = pathFinder;
        _ourNodeId = ourNodeId;
        _dummyHops = dummyHops;
    }

    /// <summary>
    /// A reply path to us whose final hop carries <paramref name="pathId"/>.
    /// </summary>
    /// <param name="pathId">Our hop's <c>path_id</c>.</param>
    /// <param name="introduction">The peer the message leaves through, preferred as the introduction node when it is an
    /// onion-message peer and no better kept (channel) peer than it: an arbitrary other peer may be one the recipient
    /// has no route to, and the reply is lost (NL-1155).</param>
    public BlindedPath Create(ReadOnlyMemory<byte> pathId, CompactPubKey? introduction = null)
    {
        var peers = _pathFinder.ListOnionMessagePeers();
        // The channel-peer preference stays first (we reconnect only to those, so the path outlives a disconnection):
        // the message's own first hop wins among equals
        var first = introduction is { } preferred && preferred != _ourNodeId && peers.Contains(preferred)
                 && (_pathFinder.HasOpenChannelWith(preferred) || !_pathFinder.HasOpenChannelWith(peers[0]))
                        ? preferred
                        : peers.Count > 0 ? peers[0] : (CompactPubKey?)null;
        IReadOnlyList<CompactPubKey> nodeIds = first is { } peer ? [peer, _ourNodeId] : [_ourNodeId];
        return _pathBuilder.CreateMessagePath(nodeIds, pathId, dummyHops: _dummyHops);
    }
}