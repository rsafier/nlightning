using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;

/// <summary>
/// Pending broadcasts whose transaction confirmed in a block the monitor processed before it tracked them (NL-779).
/// </summary>
/// <remarks>
/// <para>A processed block confirms only the pending broadcasts the monitor tracks at that moment. A row saved by
/// another component that the monitor does not follow yet (the peer's commitment handed over by the mempool reactor,
/// saved in the background while the block holding it was processed), or a row saved for a transaction that had already
/// confirmed, would otherwise stay pending for good: loaded at every start and sent after every block, refused for
/// missing inputs (its outputs spent) or as already known (its outputs unspent) forever.</para>
/// <para>When the rebroadcast round gets such a refusal (<see cref="BroadcastRefusalRules.MayBeConfirmed"/>), the
/// monitor asks bitcoind where the transaction is: <c>getrawtransaction</c>'s confirmations (with <c>txindex</c>, or
/// a wallet transaction), checked against the block's txids at that height, and otherwise the blocks from a few below
/// the row's first broadcast height up to the last processed block (at most
/// <see cref="MaxConfirmationScanBlocks"/>). A transaction found at or below the last processed block is marked
/// confirmed at that block, with the accounting event a block would have recorded, in one save; it is then no longer
/// sent. A transaction not found keeps the usual refusal rules (NL-294: a channel-output spend is never abandoned for
/// refusals). Each tracked row is looked up once per process (and again after a reorg): a block processed while the
/// monitor tracks it confirms it the usual way.</para>
/// </remarks>
public partial class BlockchainMonitorService
{
    /// <summary>How many blocks below a row's first broadcast height are searched (the row's height is the last
    /// processed block when it was saved; the transaction may have confirmed just before).</summary>
    private const uint ConfirmationScanMargin = 6;

    /// <summary>The txids already looked up (NL-779), so a refusal is answered by one lookup per process.</summary>
    private readonly HashSet<uint256> _confirmationLookups = [];

    /// <summary>The most blocks one lookup reads when bitcoind cannot say where the transaction is (no
    /// <c>txindex</c>).</summary>
    internal uint MaxConfirmationScanBlocks { get; set; } = 2016;

    /// <summary>
    /// Marks <paramref name="broadcast"/> confirmed when its transaction is in a block at or below the last processed
    /// one (see the class remarks). True when it was found (and is no longer tracked); false when it was not, was looked
    /// up before, or the lookup failed.
    /// </summary>
    private async Task<bool> TrySettleConfirmedAsync(BroadcastTransactionModel broadcast)
    {
        var txId = new uint256(broadcast.TransactionId);
        var cancellationToken = _cts?.Token ?? CancellationToken.None;

        // Under the block queue: the last processed height does not move and no block marks the row meanwhile
        await _blockBacklogSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (!_pendingBroadcasts.ContainsKey(txId) || !_confirmationLookups.Add(txId))
                return false;

            var lastProcessed = _lastProcessedBlockHeight;
            var found = await FindConfirmationAsync(txId, broadcast.FirstBroadcastHeight, lastProcessed);
            if (found is not { } confirmation)
                return false;

            var (height, blockHash) = confirmation;
            var hash = new Hash(blockHash.ToBytes());
            using (var scope = _serviceProvider.CreateScope())
            {
                using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var stored = await TryGetBroadcastForAccountingAsync(uow, broadcast.TransactionId);
                if (stored is not null)
                {
                    await uow.BroadcastTransactionDbRepository.MarkConfirmedAsync(broadcast.TransactionId, height,
                                                                                   hash);
                    if (stored.State != BroadcastState.Confirmed)
                    {
                        var effects = new BlockEffects(height, hash, new BlockHeaderModel(height, hash, Hash.Empty));
                        await CollectBroadcastConfirmedAsync(uow, stored,
                                                             Transaction.Load(broadcast.RawTransaction, _network),
                                                             effects);
                        await StageAccountingAsync(uow, effects);
                    }

                    await uow.SaveChangesAsync();
                }
            }

            broadcast.MarkConfirmed(height, hash);
            _pendingBroadcasts.TryRemove(txId, out _);
            ForgetRefusals(txId);
            _logger.LogInformation(
                "{Purpose} transaction {TxId} confirmed at height {Height} before the chain monitor followed it; it is "
              + "marked confirmed and no longer sent (NL-779)", Enum.GetName(broadcast.Purpose), txId, height);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Cannot check whether {Purpose} transaction {TxId} is confirmed",
                               Enum.GetName(broadcast.Purpose), txId);
            return false;
        }
        finally
        {
            _blockBacklogSemaphore.Release();
        }
    }

    /// <summary>
    /// The block at or below <paramref name="lastProcessed"/> that holds <paramref name="txId"/>, or null: first where
    /// bitcoind's confirmations put it, then the blocks from <see cref="ConfirmationScanMargin"/> below
    /// <paramref name="firstBroadcastHeight"/> up (at most <see cref="MaxConfirmationScanBlocks"/>).
    /// </summary>
    private async Task<(uint Height, uint256 BlockHash)?> FindConfirmationAsync(uint256 txId, uint firstBroadcastHeight,
                                                                               uint lastProcessed)
    {
        var confirmations = await _bitcoinChainService.GetTransactionConfirmationsAsync(txId);
        if (confirmations > 0)
        {
            // bitcoind knows where it is: a block at or below the last processed one, or one the queue confirms it in.
            // A block found between the two calls moves the tip by one, so the block below is checked too
            var tip = await _bitcoinChainService.GetCurrentBlockHeightAsync();
            var height = tip + 1 >= confirmations ? tip + 1 - confirmations : 0;
            uint[] candidates = height > 0 ? [height, height - 1] : [height];
            foreach (var candidate in candidates)
                if (candidate <= lastProcessed && await BlockHoldsAsync(candidate, txId) is { } blockHash)
                    return (candidate, blockHash);

            return null;
        }

        var from = firstBroadcastHeight > ConfirmationScanMargin ? firstBroadcastHeight - ConfirmationScanMargin : 0;
        for (var height = from; height <= lastProcessed && height - from < MaxConfirmationScanBlocks; height++)
            if (await BlockHoldsAsync(height, txId) is { } blockHash)
                return (height, blockHash);

        return null;
    }

    /// <summary>The hash of the block at <paramref name="height"/> when it holds <paramref name="txId"/>.</summary>
    private async Task<uint256?> BlockHoldsAsync(uint height, uint256 txId)
    {
        var block = await _bitcoinChainService.GetBlockTxIdsAsync(height);
        return block is { } found && found.TxIds.Contains(txId) ? found.BlockHash : null;
    }
}