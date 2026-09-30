namespace NLightning.Domain.Node.Interfaces;

using Bootstrap;
using Crypto.ValueObjects;

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

    /// <summary>
    /// BOLT 10 assisted location (NL-541): asks the effective seeds where <paramref name="nodeId"/> listens and
    /// returns the usable endpoints found (deduped, one per address and port), empty when no seed knows the node.
    /// The queries are recorded in <see cref="GetStatus"/> as seed queries of run 0 (not part of a bootstrap run).
    /// This dials nothing and works whether or not the bootstrap is on; in Tor-only mode the seeds are never asked
    /// (NL-542) and the answer is empty.
    /// </summary>
    /// <param name="nodeId">The node id to locate.</param>
    /// <param name="cancellationToken">The caller's token; its cancellation is the only exception thrown.</param>
    Task<IReadOnlyList<SeedPeerCandidate>> LocatePeerAsync(CompactPubKey nodeId, CancellationToken cancellationToken);
}