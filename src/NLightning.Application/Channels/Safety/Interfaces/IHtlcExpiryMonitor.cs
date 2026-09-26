namespace NLightning.Application.Channels.Safety.Interfaces;

/// <summary>
/// Applies the BOLT 2 HTLC deadlines on every new block (BOLT2 plan N9-T2): fails unresolved incoming HTLCs back
/// upstream in time and fails the channel (broadcast) when an HTLC must be resolved on chain.
/// </summary>
public interface IHtlcExpiryMonitor
{
    /// <summary>Subscribes to the chain monitor's new blocks. Call it once the channels are loaded.</summary>
    void Start();

    /// <summary>Unsubscribes and waits for the round in progress.</summary>
    Task StopAsync();

    /// <summary>
    /// Runs one round at <paramref name="height"/> over every loaded channel (what a new block triggers).
    /// </summary>
    Task CheckAsync(uint height, CancellationToken cancellationToken = default);
}