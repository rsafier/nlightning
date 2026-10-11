namespace NLightning.Domain.Bitcoin.Events;

using Crypto.ValueObjects;
using ValueObjects;

/// <summary>
/// The outpoints every non-coinbase input of a processed block spends (BOLT 7 plan G2-T5, D4: the graph pruner checks
/// them against the funding outpoints of the graph, with no bitcoind call and no watch row). Raised after the block's
/// save, also for a replayed block and for the blocks of a new branch after a reorg, so listeners must be idempotent.
/// </summary>
public sealed class BlockInputsEventArgs : EventArgs
{
    /// <summary>The height of the block.</summary>
    public uint Height { get; }

    /// <summary>The hash of the block, in internal byte order.</summary>
    public Hash BlockHash { get; }

    /// <summary>The spent outpoints (transaction id in internal byte order, output index), in block order.</summary>
    public IReadOnlyList<(TxId TransactionId, uint OutputIndex)> SpentOutpoints { get; }

    public BlockInputsEventArgs(uint height, Hash blockHash,
                                IReadOnlyList<(TxId TransactionId, uint OutputIndex)> spentOutpoints)
    {
        ArgumentNullException.ThrowIfNull(spentOutpoints);
        Height = height;
        BlockHash = blockHash;
        SpentOutpoints = spentOutpoints;
    }
}