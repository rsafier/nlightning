namespace NLightning.Domain.Node.Interfaces;

using Bootstrap;

/// <summary>
/// Finds first peers through BOLT 10 DNS seeds when the node knows none (<c>Node:Bootstrap</c>, on by default on
/// mainnet only).
/// </summary>
public interface IPeerBootstrapService
{
    /// <summary>Starts the bootstrap loop in the background and returns at once.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Cancels the loop and waits (bounded) for it to end.</summary>
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>What the bootstrap has done in this process: runs, seed queries and dials (a snapshot).</summary>
    PeerBootstrapStatus GetStatus();
}