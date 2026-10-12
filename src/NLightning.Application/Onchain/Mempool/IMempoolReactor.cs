namespace NLightning.Application.Onchain.Mempool;

/// <summary>
/// Reacts to unconfirmed spends of our channels' outputs (BOLT 5 plan O8, NL-098; BOLT 5 MAY monitor the mempool):
/// fulfills upstream as soon as a preimage appears in an unconfirmed transaction, and broadcasts our penalty right
/// behind a revoked commitment that is still in the mempool. It never treats an unconfirmed transaction as a
/// confirmation: the close, its rows and every other decision wait for the block.
/// </summary>
public interface IMempoolReactor
{
    /// <summary>
    /// Subscribes to the chain monitor's mempool spends and new blocks, and starts following the penalties prepared
    /// before a restart. Call it before the monitor starts (the host's job).
    /// </summary>
    void Start();

    /// <summary>Unsubscribes and waits for the work in progress.</summary>
    Task StopAsync();
}