using System.Collections.Concurrent;

namespace NLightning.GossipProbe;

using Application.Gossip.Relay.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Enums;
using Domain.Gossip.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;

/// <summary>
/// The relay run (D12, <c>--relay-to</c>): the relay of other nodes' gossip is on, but only toward one peer. This
/// directory lists only that peer to the relay scheduler (the other peers stay sync peers and never get relayed gossip).
/// </summary>
public sealed class RelayTargetPeerDirectory(IGossipPeerDirectory inner, CompactPubKey target) : IGossipPeerDirectory
{
    public IReadOnlyList<GossipPeer> GetConnectedPeers() =>
        inner.GetConnectedPeers().Where(p => p.NodeId == target).ToList();
}

/// <summary>
/// Counts what the relay sends (or queues on the outbox) per peer and type, what the outbox refused, and the
/// messages sent back to a peer that had sent us the very same version (echo to origin, BOLT 7 SHOULD NOT).
/// </summary>
public sealed class RelayRecorder
{
    private readonly ConcurrentDictionary<(CompactPubKey Peer, string Key), byte> _received = new();
    private readonly ConcurrentDictionary<(CompactPubKey Peer, string Kind), long> _counts = new();

    /// <summary>Remembers a gossip message a peer sent us (its version: scid for a 256, signature for a 257/258).</summary>
    public void RecordReceived(CompactPubKey peer, IMessage message)
    {
        if (KeyOf(message) is { } key)
            _received.TryAdd((peer, key), 0);
    }

    public void RecordSent(CompactPubKey peer, IMessage message, bool queued)
    {
        var kind = PeerTraffic.KindOf(message);
        Count(peer, queued ? kind : kind + ".refused");
        if (queued && KeyOf(message) is { } key && _received.ContainsKey((peer, key)))
            Count(peer, kind + ".echo_to_origin");
    }

    public IReadOnlyDictionary<string, long> Snapshot() =>
        _counts.ToDictionary(e => $"{ProbeOptions.AliasOf(e.Key.Peer.ToString())}/{e.Key.Kind}", e => e.Value);

    /// <summary>One peer's count of <paramref name="kind"/> (e.g. <c>channel_update</c>, <c>channel_update.refused</c>).</summary>
    public long Get(CompactPubKey peer, string kind) => _counts.GetValueOrDefault((peer, kind));

    public long Total(string suffix) => _counts.Where(e => e.Key.Kind.EndsWith(suffix, StringComparison.Ordinal))
                                               .Sum(e => e.Value);

    private void Count(CompactPubKey peer, string kind) =>
        _counts.AddOrUpdate((peer, kind), 1, (_, value) => value + 1);

    private static string? KeyOf(IMessage message) => message switch
    {
        ChannelAnnouncementMessage a => "256:" + a.Payload.ShortChannelId,
        ChannelUpdateMessage u => "258:" + Convert.ToHexString(u.Payload.Signature.Value),
        NodeAnnouncementMessage n => "257:" + Convert.ToHexString(n.Payload.Signature.Value),
        _ => null
    };
}

/// <summary>Records every relay send through <see cref="RelayRecorder"/>, then passes it on.</summary>
public sealed class RecordingGossipPeerSender(IGossipPeerSender inner, RelayRecorder recorder) : IGossipPeerSender
{
    private readonly ConcurrentDictionary<CompactPubKey, GossipPeer> _peers = new();

    /// <summary>The latest connection of every peer the relay sent to (for the per-peer outbox depths).</summary>
    public IReadOnlyCollection<GossipPeer> Peers => _peers.Values.ToList();

    public async ValueTask<GossipEnqueueResult> SendAsync(GossipPeer peer, IMessage message, int size)
    {
        _peers[peer.NodeId] = peer;
        var result = await inner.SendAsync(peer, message, size);
        recorder.RecordSent(peer.NodeId, message, result == GossipEnqueueResult.Queued);
        return result;
    }

    public GossipOutboxDepth? GetDepth(GossipPeer peer) => inner.GetDepth(peer);
}