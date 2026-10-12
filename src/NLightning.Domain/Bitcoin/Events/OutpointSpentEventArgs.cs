namespace NLightning.Domain.Bitcoin.Events;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using ValueObjects;

/// <summary>
/// A watched outpoint (a channel's funding output, or an output BOLT 5 must resolve) was spent in a block.
/// </summary>
public class OutpointSpentEventArgs : EventArgs
{
    /// <summary>The channel whose outpoint was spent.</summary>
    public ChannelId ChannelId { get; }

    /// <summary>The spending transaction.</summary>
    public SignedTransaction SpendingTransaction { get; }

    /// <summary>The height of the block holding the spending transaction.</summary>
    public uint BlockHeight { get; }

    /// <summary>The position of the spending transaction in its block (0 is the coinbase).</summary>
    public uint TransactionIndex { get; }

    /// <summary>The transaction of the spent outpoint, when the monitor reported it.</summary>
    public TxId? SpentTransactionId { get; }

    /// <summary>The output index of the spent outpoint, when the monitor reported it.</summary>
    public uint? SpentOutputIndex { get; }

    /// <summary>The hash of the block holding the spending transaction (internal byte order), when reported.</summary>
    public Hash? BlockHash { get; }

    public OutpointSpentEventArgs(ChannelId channelId, SignedTransaction spendingTransaction, uint blockHeight,
                                  uint transactionIndex, TxId? spentTransactionId = null,
                                  uint? spentOutputIndex = null, Hash? blockHash = null)
    {
        ArgumentNullException.ThrowIfNull(spendingTransaction);
        ChannelId = channelId;
        SpendingTransaction = spendingTransaction;
        BlockHeight = blockHeight;
        TransactionIndex = transactionIndex;
        SpentTransactionId = spentTransactionId;
        SpentOutputIndex = spentOutputIndex;
        BlockHash = blockHash;
    }
}