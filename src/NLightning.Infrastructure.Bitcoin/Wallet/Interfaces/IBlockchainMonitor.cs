namespace NLightning.Infrastructure.Bitcoin.Wallet.Interfaces;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Interfaces;

/// <summary>
/// Follows the chain block by block (ZMQ <c>rawblock</c> plus RPC catch-up): watched transactions and their depth,
/// watched outpoints (<see cref="IOutpointWatcher"/>), wallet deposits and spends, the broadcast set
/// (<see cref="IChainBroadcaster"/>) and reorgs.
/// </summary>
/// <remarks>
/// Each block is processed in one unit of work: everything it changes is saved together or not at all (NL-214), and
/// its events are raised only after that save. A reorg is detected from the ring of recent block hashes; the rows the
/// disconnected blocks changed are rolled back in one save before the new branch is processed (NL-096).
/// </remarks>
public interface IBlockchainMonitor : IChainBroadcaster, IOutpointWatcher
{
    uint LastProcessedBlockHeight { get; }

    /// <summary>
    /// True while block processing is halted on a block that keeps failing, or on a reorg deeper than the header ring
    /// (NL-097, NL-216). Nothing on chain (confirmations, deposits, spends) is seen while it is set; callers should not
    /// start new channel operations.
    /// </summary>
    bool IsChainProcessingHalted { get; }

    event EventHandler<NewBlockEventArgs> OnNewBlockDetected;
    event EventHandler<TransactionConfirmedEventArgs> OnTransactionConfirmed;
    event EventHandler<WalletMovementEventArgs>? OnWalletMovementDetected;

    /// <summary>
    /// Saves the watch and a pending <see cref="Domain.Onchain.Models.BroadcastTransactionModel"/> for the transaction
    /// (purpose <c>Unspecified</c>) in one save, then publishes it. A refused publish throws, and the stored row makes
    /// the monitor send it again after every block until it confirms.
    /// </summary>
    Task PublishAndWatchTransactionAsync(ChannelId channelId, SignedTransaction signedTransaction, uint requiredDepth);

    Task WatchTransactionAsync(ChannelId channelId, TxId txId, uint requiredDepth);

    /// <summary>
    /// Follows a watch whose row the caller already saved (in the same save as the state that needs it, so no crash
    /// leaves the state without its watch). Nothing is written.
    /// </summary>
    void TrackWatchedTransaction(WatchedTransactionModel watchedTransaction);

    /// <summary>Publishes a transaction (its watch, if any, is the caller's). Nothing is stored.</summary>
    Task PublishTransactionAsync(SignedTransaction signedTransaction);

    /// <summary>
    /// Raises <see cref="IOutpointWatcher.OnWatchedOutpointSpent"/> when a processed block spends <paramref name="txId"/>:
    /// <paramref name="outputIndex"/>. Memory only: the caller registers it again after a restart. Use
    /// <see cref="IOutpointWatcher.WatchOutpointAsync"/> for a watch that survives restarts.
    /// </summary>
    void WatchOutpointSpend(ChannelId channelId, TxId txId, uint outputIndex);

    /// <summary>Stops watching an outpoint in memory (a stored watch comes back at the next start).</summary>
    void StopWatchingOutpointSpend(TxId txId, uint outputIndex);

    void WatchBitcoinAddress(WalletAddressModel walletAddress);

    /// <summary>
    /// Loads the watches, catches up to the chain tip (the tip included, NL-215), then follows new blocks.
    /// </summary>
    /// <param name="heightOfBirth">Wallet's height of birth to avoid processing old blocks</param>
    /// <param name="cancellationToken"></param>
    Task StartAsync(uint heightOfBirth, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the background task and cancels any ongoing operations within the service.
    /// </summary>
    /// <returns>A task representing the asynchronous stop operation.</returns>
    Task StopAsync();
}