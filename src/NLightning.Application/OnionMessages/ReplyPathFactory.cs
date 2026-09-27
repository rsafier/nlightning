namespace NLightning.Application.OnionMessages;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages.Interfaces;

/// <summary>
/// Builds the <c>reply_path</c> of our own messages (BOLT 4 writer: <c>first_node_id</c> is the unblinded
/// introduction node, <c>first_path_key</c> its path key; plan D7): a two-hop path whose introduction node is a
/// connected peer that negotiated <c>option_onion_messages</c> (one we have an open channel with first), else a
/// one-hop path to ourselves. Our hop's <c>path_id</c> is the secret the <see cref="PendingReplyRegistry"/> recognizes.
/// </summary>
/// <remarks>
/// With a peer as the introduction node our node id stays hidden inside the path (plan D7: a private node should not
/// reveal it), and CLN only uses introduction nodes that advertise bit 38/39 (plan F-05).
/// </remarks>
public sealed class ReplyPathFactory
{
    private readonly IBlindedMessagePathBuilder _pathBuilder;
    private readonly OnionMessagePathFinder _pathFinder;
    private readonly CompactPubKey _ourNodeId;

    public ReplyPathFactory(IBlindedMessagePathBuilder pathBuilder, OnionMessagePathFinder pathFinder,
                            CompactPubKey ourNodeId)
    {
        _pathBuilder = pathBuilder;
        _pathFinder = pathFinder;
        _ourNodeId = ourNodeId;
    }

    /// <summary>
    /// A reply path to us whose final hop carries <paramref name="pathId"/>.
    /// </summary>
    /// <param name="pathId">Our hop's <c>path_id</c>.</param>
    public BlindedPath Create(ReadOnlyMemory<byte> pathId)
    {
        var peers = _pathFinder.ListOnionMessagePeers();
        IReadOnlyList<CompactPubKey> nodeIds = peers.Count > 0 ? [peers[0], _ourNodeId] : [_ourNodeId];
        return _pathBuilder.CreateMessagePath(nodeIds, pathId);
    }
}