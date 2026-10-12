namespace NLightning.Infrastructure.Bitcoin.Wallet.Interfaces;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;

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

    /// <summary>Why block processing halted, while <see cref="IsChainProcessingHalted"/> is set; null otherwise.</summary>
    string? ChainProcessingHaltReason { get; }

    event EventHandler<NewBlockEventArgs> OnNewBlockDetected;

    /// <summary>
    /// The outpoints a processed block's inputs spend (coinbase excluded), raised right after
    /// <see cref="OnNewBlockDetected"/> for every processed block, only when someone listens (BOLT 7 plan G2-T5: the
    /// graph pruner). Also raised for a replayed block and for a new branch after a reorg, so handlers must be
    /// idempotent; they run on the monitor's loop, so they must only enqueue.
    /// </summary>
    event EventHandler<BlockInputsEventArgs>? OnBlockInputs;

    /// <summary>
    /// A transaction bitcoind accepted into its mempool (ZMQ <c>rawtx</c>) spends a watched outpoint, or an output of a
    /// transaction reported this way (one level: a commitment and its HTLC transaction both unconfirmed). BOLT 5 plan
    /// O8 (NL-098): never a confirmation, nothing is saved and no watch is marked spent. Raised once per transaction
    /// (the monitor remembers a bounded number of txids) on the mempool loop, so handlers must only enqueue.
    /// </summary>
    event EventHandler<MempoolSpendEventArgs>? OnWatchedOutpointSpentInMempool;
    event EventHandler<TransactionConfirmedEventArgs> OnTransactionConfirmed;
    event EventHandler<WalletMovementEventArgs>? OnWalletMovementDetected;

    /// <summary>Live wallet transaction discovery, confirmation and unconfirmation. Handlers must only enqueue;
    /// confirmed and rewound observations are raised after persistence. No historical replay.</summary>
    event EventHandler<WalletTransactionEventArgs>? OnWalletTransactionObserved
    {
        add { }
        remove { }
    }

    /// <summary>Invalidates the preceding completion marker before a newly saved wallet batch is published.</summary>
    event EventHandler? OnWalletTransactionsProcessing
    {
        add { }
        remove { }
    }

    /// <summary>Raised after a committed block or rewind's wallet transaction observations. Handlers only enqueue.</summary>
    event EventHandler<NewBlockEventArgs>? OnWalletTransactionsProcessed
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Saves the watch and a pending <see cref="BroadcastTransactionModel"/> for the transaction
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

    /// <summary>
    /// Follows a pending broadcast whose row the caller saved without publishing it (NL-779: the peer's commitment the
    /// mempool reactor handed over): it is sent again after every block and marked confirmed when a processed block
    /// holds it. Nothing is written or sent; a row that is not pending is ignored.
    /// </summary>
    void TrackPendingBroadcast(BroadcastTransactionModel transaction);

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

    /// <summary>
    /// Stops watching a transaction in memory (a stored watch comes back at the next start). For a watch that can
    /// never complete any more: the funding transaction of a losing RBF attempt of a dual-funded open, whose rival
    /// spent the shared input irrecoverably (NL-529).
    /// </summary>
    void StopWatchingTransaction(TxId txId);

    void WatchBitcoinAddress(WalletAddressModel walletAddress);

    /// <summary>
    /// Loads from the database, without asking bitcoind, what the node's wallet needs before any peer can act on it
    /// (NL-600): the wallet UTXO set with its fee input reservations (spent ones ended), the wallet addresses, the
    /// pending watched transactions, the channel locks of pending fundings (NL-462) and the last processed height. A
    /// host calls it before <c>PeerManager.StartAsync</c>: a negotiation resumed by a peer right after a restart (a
    /// splice's <c>tx_signatures</c>) signs reserved wallet inputs the signer finds only in the loaded UTXO set. Runs
    /// once per start; <see cref="StartAsync"/> calls it when the host has not.
    /// </summary>
    Task LoadWalletAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the watches (<see cref="LoadWalletAsync"/>, when the host has not called it), catches up to the chain tip
    /// (the tip included, NL-215), then follows new blocks.
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