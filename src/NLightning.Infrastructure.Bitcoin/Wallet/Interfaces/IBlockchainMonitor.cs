namespace NLightning.Infrastructure.Bitcoin.Wallet.Interfaces;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;

public interface IBlockchainMonitor
{
    uint LastProcessedBlockHeight { get; }
    event EventHandler<NewBlockEventArgs> OnNewBlockDetected;
    event EventHandler<TransactionConfirmedEventArgs> OnTransactionConfirmed;
    event EventHandler<WalletMovementEventArgs>? OnWalletMovementDetected;

    /// <summary>A watched outpoint (see <see cref="WatchOutpointSpend"/>) was spent in a processed block.</summary>
    event EventHandler<OutpointSpentEventArgs>? OnWatchedOutpointSpent;

    Task PublishAndWatchTransactionAsync(ChannelId channelId, SignedTransaction signedTransaction, uint requiredDepth);
    Task WatchTransactionAsync(ChannelId channelId, TxId txId, uint requiredDepth);

    /// <summary>
    /// Follows a watch whose row the caller already saved (in the same save as the state that needs it, so no crash
    /// leaves the state without its watch). Nothing is written.
    /// </summary>
    void TrackWatchedTransaction(WatchedTransactionModel watchedTransaction);

    /// <summary>Publishes a transaction (its watch, if any, is the caller's).</summary>
    Task PublishTransactionAsync(SignedTransaction signedTransaction);

    /// <summary>
    /// Raises <see cref="OnWatchedOutpointSpent"/> when a processed block spends <paramref name="txId"/>:
    /// <paramref name="outputIndex"/>. Memory only: the caller registers it again after a restart, before the monitor
    /// starts.
    /// </summary>
    void WatchOutpointSpend(ChannelId channelId, TxId txId, uint outputIndex);

    /// <summary>Stops watching an outpoint (<see cref="WatchOutpointSpend"/>).</summary>
    void StopWatchingOutpointSpend(TxId txId, uint outputIndex);

    void WatchBitcoinAddress(WalletAddressModel walletAddress);

    /// <summary>
    /// Starts a background task to periodically refresh the fee rate
    /// </summary>
    /// <param name="heightOfBirth">Wallet's height of birth to avoid processing old blocks</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task StartAsync(uint heightOfBirth, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the background task and cancels any ongoing operations within the service.
    /// </summary>
    /// <returns>A task representing the asynchronous stop operation.</returns>
    Task StopAsync();
}