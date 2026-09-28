namespace NLightning.Domain.Node.Interfaces;

using Bootstrap;

/// <summary>
/// Finds first peers through the gossip graph and BOLT 10 DNS seeds when the node has too few (<c>Node:Bootstrap</c>,
/// on by default on mainnet only), then keeps at least <c>MinPeers</c> connected for the process lifetime (NL-547).
/// </summary>
public interface IPeerBootstrapService
{
    /// <summary>Starts the bootstrap loop and the peer-count keeper in the background and returns at once.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Cancels the loop and the keeper and waits (bounded) for them to end.</summary>
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>What the bootstrap has done in this process: runs, seed queries and dials (a snapshot).</summary>
    PeerBootstrapStatus GetStatus();
}