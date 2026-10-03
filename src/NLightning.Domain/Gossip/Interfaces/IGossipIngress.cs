namespace NLightning.Domain.Gossip.Interfaces;

using Node.Interfaces;
using Protocol.Interfaces;

/// <summary>
/// The entry point of incoming BOLT 7 graph gossip (<c>channel_announcement</c>, <c>node_announcement</c>,
/// <c>channel_update</c>; plan BOLT7 §3.3, G2-T4). The peer service hands every such message here without checking
/// it; the ingress validates it (pure checks, signatures, the funding output) on its own workers and applies it to the
/// graph.
/// </summary>
public interface IGossipIngress
{
    /// <summary>
    /// False when the graph is switched off (<c>Gossip:Enabled</c>; off by default on mainnet, plan D12): messages are
    /// then dropped and no graph dump is requested from peers.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Queues <paramref name="message"/> from <paramref name="origin"/> for validation. Never blocks and never throws
    /// for a bad message: warnings (and disconnections) go to <paramref name="origin"/> later.
    /// </summary>
    /// <returns>False when the message was dropped (disabled, not a graph message, or a full queue).</returns>
    bool TryEnqueue(IPeerService origin, IMessage message);
}