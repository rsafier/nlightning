namespace NLightning.Domain.Bitcoin.Events;

using Channels.ValueObjects;
using ValueObjects;

/// <summary>
/// A watched outpoint (a channel's funding output) was spent in a block.
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

    public OutpointSpentEventArgs(ChannelId channelId, SignedTransaction spendingTransaction, uint blockHeight,
                                  uint transactionIndex)
    {
        ArgumentNullException.ThrowIfNull(spendingTransaction);
        ChannelId = channelId;
        SpendingTransaction = spendingTransaction;
        BlockHeight = blockHeight;
        TransactionIndex = transactionIndex;
    }
}