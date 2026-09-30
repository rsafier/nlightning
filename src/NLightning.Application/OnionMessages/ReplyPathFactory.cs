namespace NLightning.Application.OnionMessages;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages.Interfaces;

/// <summary>
/// Builds the <c>reply_path</c> of our own messages (BOLT 4 writer: <c>first_node_id</c> is the unblinded
/// introduction node, <c>first_path_key</c> its path key; plan D7): a path whose introduction node is a connected peer
/// that negotiated <c>option_onion_messages</c> (one we have an open channel with first), else a one-hop path to
/// ourselves, ended by dummy hops of our own node (<see cref="OnionMessageOptions.BlindedPathDummyHops"/>, NL-525).
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
    public BlindedPath Create(ReadOnlyMemory<byte> pathId)
    {
        var peers = _pathFinder.ListOnionMessagePeers();
        IReadOnlyList<CompactPubKey> nodeIds = peers.Count > 0 ? [peers[0], _ourNodeId] : [_ourNodeId];
        return _pathBuilder.CreateMessagePath(nodeIds, pathId, dummyHops: _dummyHops);
    }
}